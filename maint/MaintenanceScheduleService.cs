using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Askou.HR.Extensions.Server.NextApi;
using Askou.HR.Extensions.Shared.ActionLog;
using AutoMapper;
using InfrastructureApp.DAL;
using InfrastructureApp.DTO.Entity;
using InfrastructureApp.Model.Models;
using InfrastructureApp.Service.Interface;
using InfrastructureAppCore.Service.Base;
using NextApi.Common.Abstractions.DAL;
using NextApi.Common.Paged;
using NextApi.Server.Entity;
using InventoryApp.Client.Service.Assets;
using InventoryApp.DTO.Entity.Assets;
using NextApi.Common.Filtering;
using Task = System.Threading.Tasks.Task;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using DocWorkflowApp.Client.Service.Base;
using DocWorkflowApp.Client.Service;
using Newtonsoft.Json;
using DocWorkflowApp.Shared.Entity;
using System.Dynamic;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using InfrastructureApp.Client.Service;
using Microsoft.IdentityModel.Tokens;

namespace InfrastructureAppCore.Service
{
    public class MaintenanceScheduleService : InfrastructureEntityService<MaintenanceScheduleDto, MaintenanceSchedule, int>, 
        IMaintenanceScheduleService
    {
        private readonly IMaintenanceScheduleAppService _maintenanceScheduleAppService;
        private readonly IMapper _mapper;
        private readonly IUnitOfWork _unitOfWork;
        private readonly IRepo<MaintenanceScheduleView, int> _viewRepository;
        private readonly IRepo<MaintenanceSchedule, int> _repo;
        private readonly IRepo<ScheduledWorkProInfos, int> _scheduledWorkProcInfosRepo;
        private readonly IRepo<EqMaintJournal, Guid> _eqMaintJournalRepo;
        private readonly IInfrastructureEntities _infrastructureEntities;
        private readonly IRepo<ScheduledWork, Guid> _scheduledWorkRepository;
        private readonly IAssetClassInventoryService _assetClassInventoryService;
        private readonly IAssetInventoryService _assetInventoryService;
        private readonly IOrderDocumentDocWorkflowService _orderDocumentDocWorkflowService;
        public MaintenanceScheduleService(
            IRepo<EqMaintJournal, Guid> eqMaintJournalRepo,
            IOrderDocumentDocWorkflowService orderDocumentDocWorkflowService,
            IRepo<ScheduledWorkProInfos, int> scheduledWorkProcInfosRepo,
            IAssetInventoryService assetInventoryService,
            IRepo<ScheduledWork, Guid> scheduledWorkRepository,
            IAssetClassInventoryService assetClassInventoryService,
            IMaintenanceScheduleAppService maintenanceScheduleAppService,
            IInfrastructureEntities infrastructureEntities,
            IUserAccessor userAccessor,
            IActionLogger actionLogger,
            IUnitOfWork unitOfWork,
            IMapper mapper,
            IRepo<MaintenanceScheduleView, int> viewRepository,
            IRepo<MaintenanceSchedule, int> repo) : base(userAccessor, actionLogger, unitOfWork, mapper, repo)
        {
            _assetInventoryService = assetInventoryService;
            _scheduledWorkRepository = scheduledWorkRepository;
            _assetClassInventoryService = assetClassInventoryService;
            _maintenanceScheduleAppService = maintenanceScheduleAppService;
            _mapper = mapper;
            _viewRepository = viewRepository;
            _unitOfWork = unitOfWork;
            _eqMaintJournalRepo = eqMaintJournalRepo;
            _scheduledWorkProcInfosRepo = scheduledWorkProcInfosRepo;
            _repo = repo;
            _infrastructureEntities = infrastructureEntities;
            _orderDocumentDocWorkflowService = orderDocumentDocWorkflowService;
        }

        protected override async Task BeforeCreate(MaintenanceSchedule entity)
        {
            await base.BeforeCreate(entity);
            await _maintenanceScheduleAppService.CreateValidationsAsync(entity);
        }

        public override async Task<PagedList<MaintenanceScheduleDto>> GetPaged(PagedRequest request)
        {
            var entitiesQuery = _viewRepository.Expand(_viewRepository.GetAll(), request.Expand);
            // apply filter
            var filterExpression = request.Filter?.ToLambdaFilter<MaintenanceScheduleView>();
            if (filterExpression != null)
            {
                entitiesQuery = entitiesQuery.Where(filterExpression);
            }

            var totalCount = entitiesQuery.Count();

            if (request.Orders != null)
                entitiesQuery = entitiesQuery.GenerateOrdering(request.Orders);

            if (request.Skip != null)
                entitiesQuery = entitiesQuery.Skip(request.Skip.Value);
            if (request.Take != null)
                entitiesQuery = entitiesQuery.Take(request.Take.Value);
            var entities = await _viewRepository.ToListAsync(entitiesQuery);


            return new PagedList<MaintenanceScheduleDto>
            {
                Items = _mapper.Map<List<MaintenanceScheduleView>, List<MaintenanceScheduleDto>>(entities),
                TotalItems = totalCount
            };
        }

        public async Task CreateMaintenanceScheduleAsync()
        {
            // Verify: if there is unfinished previous session then continue.
            var processingList = await DeleteFromScheduledWorkProcInfos();

            // If the previous session is finished successfully then generate new work list and go through it.
            if(processingList.Count == 0)
                processingList = await GenerateProcessingList();

            // Process scheduleItemList
            await ProcessScheduleItemList(processingList);

            // await DeleteFromScheduledWorkProcInfos();
        }

        private async Task<List<ScheduledWorkProInfos>> DeleteFromScheduledWorkProcInfos()
        {            

            var unprocessedWorks = _scheduledWorkProcInfosRepo.GetAll().Where(x => x.ProcResult == 0).ToList();

            // If any unprocessed works then return and try to process them
            if (unprocessedWorks.Count > 0) return unprocessedWorks;

            // Clear processing info for the new session
            try
            {
                var processedWorks = _scheduledWorkProcInfosRepo.GetAll().Where(x => x.ProcResult == 1).ToList();
                foreach (var item in processedWorks)
                {
                    await _scheduledWorkProcInfosRepo.DeleteAsync(item);
                }

                await DeleteAutoGeneratedRecords();

                await _unitOfWork.CommitAsync();

            }
            catch (Exception ex)
            {
                ex.Message.ToString();
                throw;
            }

            unprocessedWorks.Clear();
            return unprocessedWorks;
        }

        private async Task DeleteAutoGeneratedRecords()
        {

            // TODO: also delete those schedule items which are related to unprocessed Assets (partially processed).
            // Somewhat: delete from MaintenanceSchedule where assetId in {unprocessed Asset Id List}

            var entities = _repo.GetAll().ToList();
            foreach (var item in entities)
            {
                if (item.IsAutoGenerated == true)
                {
                    await _repo.DeleteAsync(item);
                }
            }
            // await _unitOfWork.CommitAsync();
        } // Удаляет все записи, которые были сгенерированы автоматически


        private async Task<List<ScheduledWorkProInfos>> GenerateProcessingList()
        {
            var assetClassIds = _scheduledWorkRepository.GetAll().Select(x => x.AssetClassId).Where(x => x != null).Distinct().ToList(); // список классов оборудования, для которых есть РР

            // Scheduled works with Asset Classes
            var scheduledWorksForClasses = _scheduledWorkRepository
                                            .GetAll()
                                            .Where(sw => sw.AssetClassId != null
                                                        && sw.AssetId == null
                                                        && (sw.Seriality >= 8000 && sw.Seriality <= 8999))
                                            .ToList();


            // Groupping Scheduled Works by Assets
            Dictionary<Guid, List<ScheduledWork>> scheduledWorksForAssets = new Dictionary<Guid, List<ScheduledWork>>();

            foreach (var scheduledWork in scheduledWorksForClasses)
            // await scheduledWorksForClasses.ForEachAsync(async scheduledWork =>
            {

                var pagedRequest = new PagedRequest()
                {
                    Filter = new FilterBuilder().Equal("AssetClassId", scheduledWork.AssetClassId.ToString()).Build()
                };

                List<AssetDto> assetsForScheduledWork = new List<AssetDto>();
                try
                {
                    assetsForScheduledWork = (await _assetInventoryService.GetPaged(pagedRequest)).Items.ToList();

                    foreach (var asset in assetsForScheduledWork)
                    {
                        var works = new List<ScheduledWork>();
                        if (!scheduledWorksForAssets.ContainsKey(asset.Id))
                        {
                            works = new List<ScheduledWork>();
                        }
                        else
                        {
                            works = scheduledWorksForAssets[asset.Id];
                        }
                        works.Add(scheduledWork);
                        scheduledWorksForAssets[asset.Id] = works;
                    }

                } catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                }


            }

            // Remove processing info for the previous session
            // await DeleteScheduledWorkProcInfos(); // Вызов метода для обнуления всех записей в таблице ScheduledWorkProcInfos

            // Prepare processing info for the currenct session
            var procInfoList = new List<ScheduledWorkProInfos>();
            foreach (var item in scheduledWorksForAssets)
            {

                foreach (var scheduledWorkI in item.Value)
                {
                    ScheduledWorkProInfos procInfo = null;
                    try
                    {
                        procInfo = new ScheduledWorkProInfos
                        {
                            ScheduledWorkId = scheduledWorkI.Id,
                            AssetId = (Guid)item.Key,
                            ProcDT = DateTime.Now,
                            ProcResult = 0,
                            ProcInfo = string.Empty
                        };

                        procInfoList.Add(procInfo);
                        await _scheduledWorkProcInfosRepo.AddAsync(procInfo);
                    } catch (Exception ex)
                    {
                        Console.WriteLine(ex.ToString());
                        procInfo.ProcInfo = ex.Message;
                        await _scheduledWorkProcInfosRepo.AddAsync(procInfo);

                    }
                }
            }
            await _unitOfWork.CommitAsync();

            return procInfoList;
        }

        private async Task ProcessScheduleItemList(List<ScheduledWorkProInfos> procInfoList)
        { 
            HashSet<Guid> processedAssetGuids = new HashSet<Guid>();
            foreach (var procInfo in procInfoList)
            {

                // If asset is processed then continue.
                if (processedAssetGuids.Where(guid => guid == procInfo.AssetId).Any()) continue;

                var assetId = procInfo.AssetId;
                // var workId = procInfo.ScheduledWorkId;

                var worksInfoForAsset = await _infrastructureEntities
                                            .ScheduledWorkProInfos
                                            .Where(info => info.AssetId == assetId && info.IsRemoved == false)
                                            .ToListAsync();

                var lastWorkDateDict = await GetLastMaitenances(assetId, worksInfoForAsset);

                // The last datetime:
                var lastMaintWork = lastWorkDateDict.Aggregate((curr, next) => curr.Value > next.Value ? curr : next).Key;
                var lastMaintDateTime = lastWorkDateDict[lastMaintWork];

                // Check for there is no maintenance at all. In such case build the schedule from current time.
                if(lastMaintDateTime.Value.CompareTo(DateTimeOffset.MinValue) == 0)
                {
                    lastMaintDateTime = DateTimeOffset.Now;
                }                     

                // DateTimes for schedule begining.
                //Dictionary<ScheduledWork, DateTimeOffset?> scheduleStartDateDict =
                //    new Dictionary<ScheduledWork, DateTimeOffset?>();

                // TODO: replace with parameter for the final scheduling time
                var finalDateForPlanning = DateTimeOffset.Now.AddYears(1);

                var (shiftedSchedule, durationByScheduleWork) = await BuildSchedule(lastWorkDateDict,
                                    lastMaintWork,
                                    lastMaintDateTime,
                                    assetId,
                                    finalDateForPlanning
                                    //,schedulesBySeriality
                                    );

                await RegisterResults(assetId, shiftedSchedule, durationByScheduleWork);

                // Register asset in work list as processed
                processedAssetGuids.Add(assetId);
            };

        }

        private async Task RegisterResults(Guid assetId, Dictionary<string, ScheduledWork> shiftedSchedule, Dictionary<ScheduledWork, double> durationByScheduleWork)
        {
            // Save to DB.
            foreach (var pair in shiftedSchedule)
            {
                var maintenanceSchedule = new MaintenanceSchedule
                {
                    IsAutoGenerated = true,
                    ScheduledWorkId = (Guid)pair.Value.Id,
                    ScheduledDT = DateTimeOffset.Parse(pair.Key).LocalDateTime,
                    CreateWOBefore = (int)pair.Value.PeriodCreateWOBefore,
                    CreatedDT = DateTime.Now,
                    TypeOfMaintenanceId = (Guid)pair.Value.TypeOfMaintenanceId,
                    AssetId = (Guid)assetId,
                    WorkScheduledDuration = (durationByScheduleWork[pair.Value] / 60 / 24).ToString() + "d", // Duration in OrderDocument is in minutes
                    WorkDocumentTemplateId = (Guid)pair.Value.WOTemplateId,
                    AssetClassId = (Guid)pair.Value.AssetClassId,
                };
                await _repo.AddAsync(maintenanceSchedule);
            }

            // Register asset in ProcInfo table as processed

            var procInfoByAsset = await _infrastructureEntities
                                    .ScheduledWorkProInfos.Where(pi => pi.AssetId == assetId).ToArrayAsync();
            foreach (var item in procInfoByAsset)
            {
                item.ProcResult = 1;
                await _scheduledWorkProcInfosRepo.UpdateAsync(item);
            }

            await _unitOfWork.CommitAsync();
        }

        private async Task<(Dictionary<string, ScheduledWork>, Dictionary<ScheduledWork, double>)> BuildSchedule(Dictionary<ScheduledWork, DateTimeOffset?> lastWorkDateDict,
                                        ScheduledWork lastMaintWork,
                                        DateTimeOffset? lastMaintDateTime,
                                        Guid assetId,
                                        DateTimeOffset finalDateForPlanning
                                        //,Dictionary<int, Dictionary<string, ScheduledWork>> schedulesBySeriality
                                        )
        {
            Dictionary<int, Dictionary<string, ScheduledWork>> schedulesBySeriality =
                new Dictionary<int, Dictionary<string, ScheduledWork>>();

            Dictionary<string, ScheduledWork> shiftedSchedule = new Dictionary<string, ScheduledWork>();

            // Build schedule from the lesser seriality to bigger.
            lastWorkDateDict = lastWorkDateDict.OrderBy(w => w.Key.Seriality).ToDictionary(w => w.Key, w => w.Value);
            int minimalSeriality = 0;
            List<int> serialityList = new List<int>();

            foreach (var pair in lastWorkDateDict)
            {

                var scheduleWork = pair.Key;
                var seriality = scheduleWork.Seriality;
                if (minimalSeriality == 0) minimalSeriality = seriality;
                serialityList.Add(seriality);

                if (seriality <= lastMaintWork.Seriality)
                {
                    // scheduleStartDateDict[scheduleWork] = lastMaintDateTime;
                    TimeSpan schedulingPeriod = await GetPeriodByReadings(assetId
                                                        , scheduleWork.ReadingTypeId
                                                        , scheduleWork.ReadingsValue
                                                        );
                    if (schedulingPeriod == default) continue;

                    Dictionary<string, ScheduledWork> schedule = new Dictionary<string, ScheduledWork>();
                    DateTimeOffset runningScheduleDate = (DateTimeOffset)lastMaintDateTime;
                    if(runningScheduleDate.CompareTo(DateTimeOffset.MinValue) == 0)
                    {
                        // No maintenance yet
                        runningScheduleDate = DateTimeOffset.Now;
                    }

                    do
                    {
                        runningScheduleDate = (DateTimeOffset)(runningScheduleDate + schedulingPeriod);
                        schedule[runningScheduleDate.ToString()] = scheduleWork;

                        var comparingRes = runningScheduleDate.CompareTo(finalDateForPlanning);
                        if (comparingRes > 0) break;

                    } while (true);

                    // schedule = schedule.OrderBy(w => w.Key).ToDictionary(w => w.Key, w => w.Value);

                    schedulesBySeriality[scheduleWork.Seriality] = schedule;

                }
                else
                {
                    DateTimeOffset lastMaintDateTimeI = (DateTimeOffset)pair.Value;

                    if(lastMaintDateTimeI.CompareTo(DateTimeOffset.MinValue) == 0)
                    {
                        lastMaintDateTimeI = DateTimeOffset.Now;
                    }

                    var readingValue = scheduleWork.ReadingsValue;

                    TimeSpan schedulingPeriod = await GetPeriodByReadings(assetId, scheduleWork.ReadingTypeId, readingValue);
                    if (schedulingPeriod == default) continue;

                    var nextMaintDateTimeI = lastMaintDateTimeI.Add(schedulingPeriod);

                    // Align with shortest works.
                    var shortestWorksDates = schedulesBySeriality[minimalSeriality]
                                                .Keys
                                                .Select(i => DateTimeOffset.Parse(i))
                                                .OrderBy(d => d)
                                                .ToArray();


                    int i = 0;
                    if (shortestWorksDates.Length <= 1) continue; // Protection. There should not be so in real life.
                    DateTimeOffset currDate = default;
                    DateTimeOffset nextDate = default;

                    while(i < shortestWorksDates.Length - 1)
                    {
                        currDate = shortestWorksDates[i];
                        nextDate = shortestWorksDates[i + 1];

                        if (nextMaintDateTimeI.CompareTo(currDate) >= 0
                                && nextMaintDateTimeI.CompareTo(nextDate) <= 0)
                            break;
                        i++;
                    }

                    // Round to nearest short cycle period boarder
                    var leftTimeSpan = nextMaintDateTimeI - currDate;
                    var rightTimeSpan = nextDate - nextMaintDateTimeI;
                    if (leftTimeSpan > rightTimeSpan)
                    {
                        nextMaintDateTimeI = nextDate;
                    }
                    else
                    {
                        nextMaintDateTimeI = currDate;
                    }

                    Dictionary<string, ScheduledWork> schedule = new Dictionary<string, ScheduledWork>();
                    DateTimeOffset runningScheduleDate = (DateTimeOffset)nextMaintDateTimeI;
                    schedule[runningScheduleDate.ToString()] = scheduleWork;
                    while(runningScheduleDate.CompareTo(finalDateForPlanning) <= 0)
                    {
                        runningScheduleDate = (DateTimeOffset)(runningScheduleDate + schedulingPeriod);
                        schedule[runningScheduleDate.ToString()] = scheduleWork;
                    };

                    // schedule = schedule.OrderBy(w => w.Key).ToDictionary(w => w.Key, w => w.Value);

                    schedulesBySeriality[scheduleWork.Seriality] = schedule;
                }
            }

            // Put all schedules in one. Go by the cycle with minimal periods.
            // Replace schedule item with item which has the maximum seriality

            if (schedulesBySeriality.Count == 0) return (shiftedSchedule, null);

            var shortestCycle = schedulesBySeriality[minimalSeriality];
            serialityList = serialityList.OrderBy(s => s).ToList();

            Dictionary<string, ScheduledWork> combinedSchedule = new Dictionary<string, ScheduledWork>();

            // Copy shortest cycle
            foreach (var pair in shortestCycle)
            {
                combinedSchedule[pair.Key] = pair.Value;
            }

            foreach (var pair in shortestCycle)
            {
                for (int i = 1; i < serialityList.Count; i++)
                {
                    var seriality = serialityList[i];
                    var schedule = schedulesBySeriality[seriality];
                    // Overwrite with schedule with the greatest seriality
                    if (schedule.ContainsKey(pair.Key))
                    {
                        combinedSchedule[pair.Key] = schedule[pair.Key];
                    }
                }
            }

            var durationByScheduleWork = await GetDurationsByScheduleWork(lastWorkDateDict);

            
            foreach (var pair in combinedSchedule)
            {
                var initialDateTime = DateTimeOffset.Parse(pair.Key);
                var shiftDuration = durationByScheduleWork[pair.Value];
                var shiftedDateTime = initialDateTime.AddMinutes(shiftDuration);
                shiftedSchedule[shiftedDateTime.ToString()] = pair.Value;
            }
            return (shiftedSchedule, durationByScheduleWork);
        }

         private async Task<Dictionary<ScheduledWork, double>> GetDurationsByScheduleWork(Dictionary<ScheduledWork, DateTimeOffset?> lastWorkDateDict)
         {
            // Shift works on their durations.
            // Get durations for each scheduled work.
            Dictionary<ScheduledWork, double> durationByScheduleWork = new Dictionary<ScheduledWork, double>();
            foreach (var pair in lastWorkDateDict)
            {
                var woTemplateId = pair.Key.WOTemplateId;
                if (woTemplateId != null)
                {
                    var orderDocument = await _orderDocumentDocWorkflowService.GetById((Guid)woTemplateId); // Вызов метода для получения шаблона РР
                    var duration = orderDocument.ScheduledDuration;
                    if (duration != null)
                        durationByScheduleWork[pair.Key] = (double)duration;
                    else
                        durationByScheduleWork[pair.Key] = 0;
                }
            }

            return durationByScheduleWork;
         }
        

        private async Task<Dictionary<ScheduledWork, DateTimeOffset?>> GetLastMaitenances(Guid assetId, List<ScheduledWorkProInfos> worksInfoForAsset)
        {
            Dictionary<ScheduledWork, DateTimeOffset?> lastWorkDateDict = new Dictionary<ScheduledWork, DateTimeOffset?>();

            // Looking for the last accomplished work
            foreach (var work in worksInfoForAsset)
            {
                var scheduleWork = await _infrastructureEntities
                    .ScheduledWorks
                    .Where(sw => sw.Id == work.ScheduledWorkId)
                    .FirstOrDefaultAsync();

                // Get last maintenance from the maintenance journal
                var lastMaintenance = await _infrastructureEntities.EqMaintJournal
                    .Where(x => x.ScheduledWorkId == work.ScheduledWorkId && x.AssetId == assetId)
                    .OrderByDescending(x => x.MaintDoneDateTime)
                    //.Select(x => x.ScheduledWorkId)
                    //.Distinct()
                    .FirstOrDefaultAsync();

                if (lastMaintenance != null)
                {
                    lastWorkDateDict[scheduleWork] = lastMaintenance.MaintDoneDateTime;
                }
                else
                {
                    // No maintenance yet
                    lastWorkDateDict[scheduleWork] = DateTimeOffset.MinValue;
                }

            }

            return lastWorkDateDict;
        }

        private async Task<TimeSpan> GetPeriodByReadings(Guid assetId, Guid? readingTypeId, double readingValue)
        {
            TimeSpan result = default;

            // Get reading value a year ago or the earliest
            var aYearAgo = DateTimeOffset.Now.AddYears(-1);
            var oldReading = await _infrastructureEntities
                                .EntityReadings
                                .Where(er => er.ReadingTypeId== readingTypeId 
                                                && er.OccuredAt <= aYearAgo
                                                && er.EntityGuidId == assetId
                                                )
                                .OrderByDescending(er => er.OccuredAt)
                                .FirstOrDefaultAsync();
            if(oldReading == null) 
            {
                oldReading = await _infrastructureEntities
                                .EntityReadings
                                .Where(er => er.ReadingTypeId == readingTypeId 
                                        && er.EntityGuidId == assetId)
                                .OrderBy(er => er.OccuredAt)
                                .FirstOrDefaultAsync();
            }

            if(oldReading == null)
            {
                // No reading at all. Return 0
                return result;
            }
            
            var currReading = await _infrastructureEntities
                                .EntityReadings
                                .Where(er => er.ReadingTypeId == readingTypeId
                                                && er.EntityGuidId == assetId)
                                .OrderByDescending(er => er.OccuredAt)
                                .FirstOrDefaultAsync();

            var readingDiff = Double.Parse(currReading.Value) - Double.Parse(oldReading.Value);
            var periodSpanInDays = (currReading.OccuredAt - oldReading.OccuredAt).Days;

            if (periodSpanInDays == 0) return result; // the only value. Cannot calculate daily reading

            var dailyReading = readingDiff / periodSpanInDays;
            result = TimeSpan.FromDays(readingValue / dailyReading);

            return result;
        }

        private async Task DeleteScheduledWorkProcInfos()
        {
            var entities = _scheduledWorkProcInfosRepo.GetAll().ToList();
            foreach (var item in entities)
            {
                await _scheduledWorkProcInfosRepo.DeleteAsync(item);
            }
        } // Удаляет все записи в таблице ScheduledWorkProcInfos

        private async Task LastScheduledWorkFromJournal()
        {
            var ids = _infrastructureEntities.EqMaintJournal
                .Where(x => x.ScheduledWork != null)
                .OrderByDescending(x => x.MaintDoneDateTime)
                .Select(x => x.ScheduledWorkId)
                .Distinct()
                .ToList();


            var eqMaintJournalList = _eqMaintJournalRepo.GetAll().Where(x => ids.Contains(x.ScheduledWorkId)).ToList()
                .GroupBy(x => x.ScheduledWorkId).Select(x => x.First()).ToList(); //#TODO Refactor
            var scheduledWorkList = _scheduledWorkRepository.GetAll().Where(x => ids.Contains(x.Id)).ToList();

            var dictOut8000 = new Dictionary<ScheduledWork, EqMaintJournal>(); // Словарь для записей, где Seriality not from 8000 to 8999
            var dictIn8000 = new Dictionary<ScheduledWork, EqMaintJournal>(); // Словарь для записей, где Seriality from 8000 to 8999
            foreach (var item in scheduledWorkList)
            {
                foreach (var eqMaintJournal in eqMaintJournalList)
                {
                    if (item.Seriality >= 8000 || item.Seriality <= 8999)
                    {
                        if (item.Id == eqMaintJournal.ScheduledWorkId)
                        {
                            dictIn8000.Add(item, eqMaintJournal);
                        }
                    }
                    else
                    {
                        if (item.Id == eqMaintJournal.ScheduledWorkId)
                        {
                            dictOut8000.Add(item, eqMaintJournal);
                        }
                    }
                }
            } // определение даты последнего обслуживания для каждого вида РР, где Seriality from 8000 to 8999 и где Seriality not from 8000 to 8999

            await DetermMaintenancePeriod(dictIn8000); // Вызов метода определения периода обслуживания единицы оборудования
        }
        private async Task DetermMaintenancePeriod(Dictionary<ScheduledWork, EqMaintJournal> dictIn8000)
        {
            // если внутри dictIn8000.Key есть только PeriodType
            var dateDict = new Dictionary<ScheduledWork, List<DateTime>>();
            foreach (var item in dictIn8000)
            {
                //if (item.Key.PeriodType != null && item.Key.ReadingTypeId == null)
                //{
                //    var dateList = new List<DateTime>();
                //    var oneYearAfter = item.Value.MaintDoneDateTime.AddYears(1);
                //    var nextDate = (item.Value.MaintDoneDateTime.AddDays(TakePeriod(item.Value.MaintDoneDateTime.DateTime, item.Key.PeriodType, item.Key.PeriodValue ?? 0))).DateTime;
                //    while (nextDate < oneYearAfter)
                //    {
                //        dateList.Add(nextDate);
                //        nextDate = nextDate.AddDays(TakePeriod(nextDate, item.Key.PeriodType, item.Key.PeriodValue ?? 0));
                //    }
                //    if (dateList.Any())
                //    {
                //        dateDict.Add(item.Key, dateList);
                //    }
                //}
                //if (item.Key.ReadingTypeId != null && item.Key.PeriodType == null)
                //{
                var dateList = new List<DateTime>();
                var oneYearAfter = item.Value.MaintDoneDateTime.AddYears(1);
                var periodByValue = (_infrastructureEntities.EntityReadings
                    .Where(x => x.ReadingTypeId == item.Key.ReadingTypeId && x.EntityGuidId == item.Key.AssetClassId)).First();
                var period = (DateTime.Now - periodByValue.OccuredAt);
                var nextDate = (item.Value.MaintDoneDateTime.AddHours(period.TotalHours)).DateTime;
                while (nextDate > oneYearAfter)
                {
                    dateList.Add(nextDate);
                    nextDate = nextDate.AddHours(period.TotalHours);
                }
                if (dateList.Any())
                {
                    dateDict.Add(item.Key, dateList);
                }
                //}
            }
            dateDict = dateDict.OrderByDescending(x => x.Key.Seriality).ToDictionary(x => x.Key, x => x.Value); // Сортировка словаря по Seriality
                                                                                                                // объединяем values 
            if (dateDict.Count == 0)
            {
                return;
            }
            dateDict[dateDict.Keys.First()] = dateDict.Values.SelectMany(x => x).Distinct().ToList();

            dateDict = dateDict.Take(1).ToDictionary(x => x.Key, x => x.Value);
            await CreateMaintSchedule(dateDict); // Вызов метода для создания записей в таблице MaintenanceSchedule
        } // Определение периода обслуживания единицы оборудования

        private async Task CreateMaintSchedule(Dictionary<ScheduledWork, List<DateTime>> dateDict)
        {
            var orderDocument = await _orderDocumentDocWorkflowService.GetById((Guid)dateDict.Keys.First().WOTemplateId); // Вызов метода для получения шаблона РР
            var duration = orderDocument.ScheduledDuration;
            var isAdded = false;
            foreach (var item in dateDict.Values)
            {
                foreach (var date in item)
                {
                    try
                    {
                        var maintenanceSchedule = new MaintenanceSchedule
                        {
                            IsAutoGenerated = true,
                            ScheduledWorkId = dateDict.Keys.First().Id,
                            ScheduledDT = date,
                            CreateWOBefore = (int)dateDict.Keys.First().PeriodCreateWOBefore,
                            CreatedDT = DateTime.Now,
                            TypeOfMaintenanceId = dateDict.Keys.First().TypeOfMaintenanceId,
                            AssetId = (Guid)dateDict.Keys.First().AssetId,
                            WorkScheduledDuration = duration / 60 + "h",
                            WorkDocumentTemplateId = (Guid)dateDict.Keys.First().WOTemplateId,
                            AssetClassId = (Guid)dateDict.Keys.First().AssetClassId,
                        };
                        await _repo.AddAsync(maintenanceSchedule);
                        isAdded = true;
                    }
                    catch (Exception e)
                    {
                        var exception = _infrastructureEntities.ScheduledWorkProInfos.Where(x => x.ScheduledWorkId == dateDict.Keys.First().Id).ToList();
                        foreach (var message in exception)
                        {
                            message.ProcInfo = e.Message;
                        }
                        isAdded = false;
                        await _scheduledWorkProcInfosRepo.AddAsync(exception);
                    }
                }
            }
            if (isAdded)
            {
                var result = await _infrastructureEntities.ScheduledWorkProInfos.Where(x => x.ScheduledWorkId == dateDict.Keys.First().Id).FirstOrDefaultAsync();
                result.ProcResult = 1;
                await _scheduledWorkProcInfosRepo.UpdateAsync(result);
                await _unitOfWork.CommitAsync();
            }
            else
            {
                return;
            }
        }
        private int TakePeriod(DateTime date, string periodType, int periodValue)
        {
            switch (periodType)
            {
                case "WORKDAYS-X":
                case "DAYS-X":
                case "WORKDAYS-R":
                case "DAYS-R":
                case "WEEKDAYS-X":
                case "WEEKDAYS-R":
                    return date.AddDays(periodValue).Day;
                case "WKS-X":
                case "WKS-R":
                    return date.AddDays(periodValue * 7).Day;
                case "MOS-X":
                case "MOS-R":
                    return date.AddMonths(periodValue).Day;
                case "YRS-X":
                case "YRS-R":
                case "HRS-X":
                    return date.AddHours(periodValue).Day;
                default:
                    return date.Day;
            }
        }
    }
}
