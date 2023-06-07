using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Abitech.Assets.Core.Misc;
using Abitech.Inventory.Core.Contracts.Assets;
using Abitech.Inventory.Core.Misc;
using Abitech.Assets.Core.Models.Assets;
using Abitech.Assets.Core.Models.Common;
using Abitech.Assets.Core.Repos;
using Abitech.Assets.Core.ViewModels;
using Abitech.Inventory.Core.Services;
using Abitech.Utility.Core.Contracts;
using Abitech.Utility.Core.Misc;
using Abitech.Utility.Core.Presenters;
using Abitech.Utility.Core.Services;
using Autofac;
using InventoryApp.Base.Constants;
using InventoryApp.Base.Enum;
using InventoryApp.Base.Security;
using Serilog;
using SkiaSharp;

namespace Abitech.Inventory.Core.Presenters.Assets
{
    public class AssetDetailsPresenter : FilterPresenter<IAssetDetailsView>
    {
        private Guid _assetId;
        private Asset _asset;
        private IList<ChildAssetListViewModel> _childAssetList;

        private int? _newOrgUnitId;
        private int? _newLocationId;
        private string _newSerialNumber;
        private bool _userCanEdit;

        public IAuthenticationService AuthService { get; }
        public IMyHelperService MyHelperService { get; }
        public IPathResolver PathResolver { get; }
        public ICodeService CodeService { get; }
        public ITagLocatorService TagLocatorService { get; }
        public IPermissionService PermissionService { get; }

        private readonly IAssetRepo _assetRepo;
        private readonly ICodeRepo _codeRepo;
        private readonly IAssetFileRepo _assetFileRepo;
        private readonly IAssetAttributeRepo _assetAttributeRepo;
        
        public AssetDetailsPresenter(
            IAuthenticationService authService,
            IMyHelperService myHelperService,
            IPathResolver pathResolver,
            ICodeService codeService,
            ITagLocatorService tagLocatorService,
            IPermissionService permissionService,
            IAssetRepo assetRepo, 
            ICodeRepo codeRepo,
            IAssetFileRepo assetFileRepo,
            IAssetAttributeRepo assetAttributeRepo,
            ITextService textService,
            IHelperService helperService,
            INaviService naviService) : base(textService, helperService, naviService)
        {
            AuthService = authService;
            MyHelperService = myHelperService;
            PathResolver = pathResolver;
            CodeService = codeService;
            TagLocatorService = tagLocatorService;
            PermissionService = permissionService;
            _assetRepo = assetRepo;
            _codeRepo = codeRepo;
            _assetFileRepo = assetFileRepo;
            _assetAttributeRepo = assetAttributeRepo;
            ViewId = new Guid("7e48c8a1-37f6-4129-8f66-312eb54f31fc");
        }

        public override void InitUi()
        {
            var view = View;
            if (view == null)
                return;

            Task.Run(async () =>
            {
                try
                {
                    _assetId = (Guid) Args[0];
                    view.ProgressBarVisible = true;
                    var asset = await _assetRepo.GetAsync(
                        _assetId, 
                        new AssetInclude
                        {
                            OrgUnit = true,
                            Location = true,
                            Class = true,
                            Type = true,
                            Status = true,
                            Codes = true,
                            Files = true,
                            AccountingType = true
                        });
                    if (asset == null) return;
                    
                    _asset = asset;
                    _childAssetList = await _assetRepo.GetChildAssetListViewModelAsync(_assetId);
                    view.Name = asset.Name;
                    view.SerialNumber = asset.SerialNumber;
                    view.InventoryNumber = asset.InventoryNumber;
                    view.SubInventoryNumber = asset.SubInventoryNumber;
                    view.AssetClass = asset.AssetClass?.Description;
                    view.AssetType = asset.AssetType?.Name;
                    view.CommissioningDate = asset.CommissioningDate;
                    view.IssueDate = asset.IssueDate;
                    view.MaintenancePeriod = asset.MaintenancePeriod;
                    var description = true switch
                    {
                        _ when string.IsNullOrEmpty(asset.AccountingType?.Description) => TextService.GetString("missing"),
                        _ when asset.AccountingType?.Id == AccountingTypes.BalanceId => TextService.GetString("balance"),
                        _ when asset.AccountingType?.Id == AccountingTypes.ImbalanceId => TextService.GetString("imbalance"),
                        _ => asset.AccountingType.Description
                    };
                    view.AccountingType = description;
                    view.OrgUnitFilter = asset.OrgUnit?.Description;
                    view.LocationFilter = asset.Location?.Description;
                    if (asset.OrgUnitId.HasValue)
                    {
                        NaviService.SetCurrentUiOrgUnit(ViewId, asset.OrgUnitId.Value);
                        NaviService.SetCurrentUiLocation(ViewId, asset.LocationId);
                    }
                    else
                        NaviService.SetCurrentUiOrgUnitToNull(ViewId, asset.LocationId);

                    _newOrgUnitId = asset.OrgUnitId;
                    _newLocationId = asset.LocationId;

                    view.IsReceipted = asset.ObjectStatusId == AssetObjectStatusEnum.Receipted;
                    view.CustodianUser = asset.CustodianUser?.FullName;
                    view.UtilizerUser = asset.UtilizerUser?.FullName;

                    if (asset.ParentId.HasValue)
                    {
                        var parentAsset = await _assetRepo.GetAsync(asset.ParentId.Value);
                        view.ParentAssetName = parentAsset?.Name;
                    }
                    else
                        view.ParentAssetName = null;
                    
                    view.ChildAssetList = _childAssetList;
                    view.CodeList = asset.Codes;
                    view.AssetFileList = asset.AssetFiles;

                    var assetAttributeList = await _assetAttributeRepo.GetViewModelListAsync(_assetId);
                    view.AssetAttributeList = assetAttributeList;
                    
                    view.CoordinatesExist = asset.Latitude.HasValue && asset.Longitude.HasValue;

                    var currentUser = await AuthService.GetUserInfoAsync();
                    _userCanEdit = asset.ObjectStatusId != AssetObjectStatusEnum.WrittenOff
                                   && (!asset.CustodianUserId.HasValue
                                   || asset.CustodianUserId.Value == currentUser.Id
                                   || await PermissionService.CheckPermissionAsync(Permissions.MoveAllAssets.NormalizePermission()));
                    if (_userCanEdit && asset.CustodianUserId.HasValue && asset.CustodianUserId.Value != currentUser.Id)
                        NaviService.OverrideUser(ViewId, asset.CustodianUserId.Value);
                }
                catch (Exception e)
                {
                    Log.Warning(e, nameof(InitUi));
                }
                finally
                {
                    view.ProgressBarVisible = false;
                }
            });
        }

        public override void ShowOrgUnitFilterDialog()
        {
            if (_userCanEdit)
                base.ShowOrgUnitFilterDialog();
            else
                View.DisplaySuccessMessage(TextService.GetString("notCustodian"));
        }

        public override void ShowLocationFilterDialog(bool orgUnitToNull = false)
        {
            if (_userCanEdit)
                base.ShowLocationFilterDialog(orgUnitToNull);
            else
                View.DisplaySuccessMessage(TextService.GetString("notCustodian"));
        }

        public void ClearFilterClicked(MyFilterName filterName)
        {
            var view = View;
            if (view == null)
                return;
            
            Task.Run(async () =>
            {
                switch (filterName)
                {
                    case MyFilterName.OrgUnit:
                        if (_asset.ObjectStatusId == AssetObjectStatusEnum.Receipted)
                        {
                            view.DisplaySuccessMessage(TextService.GetString("receiptedSpecifyOrgUnit"));
                            return;
                        }
                        if (_userCanEdit)
                            await ClearUiOrgUnitAsync();
                        else
                            View.DisplaySuccessMessage(TextService.GetString("notCustodian"));
                        break;
                    case MyFilterName.Location:
                        if (_userCanEdit)
                            await ClearUiLocationAsync();
                        else
                            View.DisplaySuccessMessage(TextService.GetString("notCustodian"));
                        break;
                }
            });
        }

        protected override async Task ClearUiOrgUnitAsync()
        {
            NaviService.SetCurrentUiOrgUnitToNull(ViewId, null);
            await UpdateOrgUnitFilterAsync();
            await UpdateLocationFilterAsync();
        }

        protected override async Task ClearUiLocationAsync()
        {
            NaviService.SetCurrentUiLocation(ViewId, null);
            await UpdateOrgUnitFilterAsync();
            await UpdateLocationFilterAsync();
        }

        protected override async Task UpdateOrgUnitFilterAsync()
        {
            var view = View;
            if (view == null)
                return;

            var orgUnit = await NaviService.GetCurrentUiOrgUnitAsync(ViewId);
            view.OrgUnitFilter = orgUnit?.Description;
            _newOrgUnitId = orgUnit?.Id;
            UpdateSaveButton(view);
        }

        protected override async Task UpdateLocationFilterAsync()
        {
            var view = View;
            if (view == null)
                return;
            
            var useLocationOrgUnit = !(await NaviService.GetCurrentUiOrgUnitIdAsync(ViewId)).HasValue;
            var location = await NaviService.GetCurrentUiLocationAsync(ViewId, useLocationOrgUnit);
            view.LocationFilter = location?.Description;
            _newLocationId = location?.Id;
            
            if (location != null && useLocationOrgUnit)
            {
                view.OrgUnitFilter = location.OrgUnit?.Description;
                _newOrgUnitId = location.OrgUnitId;
            }
            UpdateSaveButton(view);
        }

        public void UpdateButtons()
        {
            var view = View;
            if (view == null)
                return;
            
            try
            {
                UpdateSaveButton(view);
            }
            catch (Exception e)
            {
                Log.Warning(e, nameof(UpdateButtons));
            }
        }

        private void UpdateSaveButton(IAssetDetailsView view)
        {
            view.SaveButtonVisible = HasUnsavedData();
        }

        public async void GoBackClicked()
        {
            var view = View;
            if (view == null)
                return;
            
            try
            {
                if (!HasUnsavedData())
                {
                    view.CloseView();
                    return;
                }

                var okCancel = await view.DisplayOkCancelDialogAsync(
                    null,
                    TextService.GetString("unsavedDataSureGoBack"),
                    TextService.GetString("yes"),
                    TextService.GetString("cancel"));
                if (okCancel != OkCancel.Ok)
                    return;
                view.CloseView();
            }
            catch (Exception e)
            {
                Log.Warning(e, nameof(GoBackClicked));
            }
        }

        private bool HasUnsavedData()
        {
            var has = _asset?.OrgUnitId != _newOrgUnitId 
                      || _asset?.LocationId != _newLocationId
                      || _asset?.SerialNumber != _newSerialNumber;
            return has;
        }

        public async void Save()
        {
            var view = View;
            if (view == null)
                return;
            
            try
            {
                if (!HasUnsavedData())
                {
                    view.CloseView();
                    return;
                }

                var okCancel = await view.DisplayOkCancelDialogAsync(
                    null,
                    TextService.GetString("sureSaveChanges"),
                    TextService.GetString("yes"),
                    TextService.GetString("cancel"));
                if (okCancel != OkCancel.Ok)
                    return;

                if (_asset.CustodianUserId.HasValue)
                {
                    if (!_userCanEdit)
                    {
                        view.DisplaySuccessMessage(TextService.GetString("notCustodian"));
                        return;
                    }
                    
                    if (!_newOrgUnitId.HasValue)
                    {
                        view.DisplaySuccessMessage(TextService.GetString("receiptedSpecifyOrgUnit"));
                        return;
                    }
                }

                if (_asset.OrgUnitId != _newOrgUnitId)
                    _asset.SetOrgUnitId(_newOrgUnitId);

                if (_asset.LocationId != _newLocationId)
                    _asset.SetLocationId(_newLocationId);

                view.CloseView();
            }
            catch (Exception e)
            {
                Log.Warning(e, nameof(Save));
                view.DisplaySuccessMessage(TextService.GetString("errorOccured"));
            }
        }

        public CodeActionsVisibility GetCodeActionsVisibility(int codeIndex)
        {
            var visibility = new CodeActionsVisibility();
            if (codeIndex < 0)
                return visibility;
            
            var code = _asset.Codes[codeIndex];
            if (code == null)
            {
                Log.Warning("Code is null!");
                return visibility;
            }

            visibility.AddCodeVisible = true;
            visibility.DeleteCodeVisible = true;
            visibility.FindCodeVisible = MyHelperMethods.LocateTagVisible(code);
            return visibility;
        }

        public AssetFileActionsVisibility GetAssetFileActionsVisibility(int assetFileIndex)
        {
            var visibility = new AssetFileActionsVisibility();

            if (_asset.AssetFiles == null 
                || _asset.AssetFiles.Count <= 0
                || _asset.AssetFiles != null 
                && _asset.AssetFiles.Count < 10)
                visibility.AddAssetFileVisible = true;

            if (_asset.AssetFiles == null || _asset.AssetFiles.Count <= 0 || assetFileIndex < 0) 
                return visibility;
            
            var filePath = _asset.AssetFiles[assetFileIndex]?.GetLocalPath(PathResolver);
            visibility.DeleteAssetFileVisible = !string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath);

            return visibility;
        }

        public async void FindCode(int codeIndex)
        {
            var view = View;
            if (view == null)
                return;
            
            if (codeIndex < 0)
                return;
            
            try
            {
                var code = _asset.Codes[codeIndex];
                if (code == null)
                {
                    Log.Warning("Code is null!");
                    return;
                }

                await TagLocatorService.LocateTag(view, new List<Code> {code});
            }
            catch (Exception e)
            {
                Log.Warning(e, nameof(FindCode));
                view.DisplaySuccessMessage(TextService.GetString("errorOccured"));
            }
        }

        private async Task<bool> CheckPermissions(Permissions action)
        {
            var view = View;
            if (view == null)
                return false;
            
            var hasClaim = await PermissionService.CheckPermissionAsync(action.NormalizePermission());
            if (!hasClaim)
            {
                view.DisplayErrorMessage(TextService.GetString("noClaimsForAction"), "");
            }

            return hasClaim;
        }

        public async void TryAddAssetCode()
        {
            var view = View;
            if (view == null)
                return;
            
            try
            {
                var hasClaim = await CheckPermissions(Permissions.Code_Add);
                if (!hasClaim) return;
                
                CodeService.CodeChosen += HandleChosenTag;
                await CodeService.ShowChooseCodeDialogAsync(view, true);
            }
            catch (Exception e)
            {
                Log.Warning(e, nameof(TryAddAssetCode));
                view.DisplayErrorMessage(TextService.GetString("errorOccured"), e.Message);
            }
           
            void HandleChosenTag(object sender, CodeChosenEventArgs codeArgs)
            {
                try
                {
                    CodeService.CodeChosen -= HandleChosenTag;

                    switch (codeArgs.Error)
                    {
                        case CodeChosenError.NoError:
                            break;
                        case CodeChosenError.Canceled:
                            return;
                        case CodeChosenError.ReaderNotConnected:
                            view.DisplayErrorMessage(TextService.GetString("deviceNotConnected"),
                                codeArgs.Error.ToString());
                            return;
                        case CodeChosenError.CouldNotReadCodes:
                            view.DisplaySuccessMessage(TextService.GetString("noTagsRead"));
                            return;
                        case CodeChosenError.CouldNotConvertCodes:
                        case CodeChosenError.ChooseReadTypeError:
                            view.DisplayErrorMessage(TextService.GetString("errorOccured"), codeArgs.Error.ToString());
                            return;
                    }

                    if (codeArgs.ChosenCodes == null || codeArgs.ChosenCodes.Count != 1)
                    {
                        view.DisplaySuccessMessage(TextService.GetString("noTagsRead"));
                        return;
                    }
                    AddAssetCode(codeArgs.CodeType.Convert(), codeArgs.ChosenCodes[0]);
                }
                catch(Exception e)
                {
                    Log.Warning(e, nameof(HandleChosenTag));
                }
            }
        }

        public async void TryDeleteAssetCode(int codeIndex)
        {
            try
            {
                var hasClaim = await CheckPermissions(Permissions.Code_Delete);
                if (!hasClaim) return;
            }
            catch (Exception e)
            {
                Log.Warning(e, nameof(TryDeleteAssetCode));
                return;
            }
            
            View?.DisplayOkCancelDialog(null, TextService.GetString("sureDeleteEpc"), okCancel =>
            {
                if (okCancel != OkCancel.Ok) return;
                
                DeleteAssetCode(codeIndex);
            });
        }
        
        public async void TryDeleteAssetFile(int assetFileIndex)
        {
            try
            {
                var hasClaim = await CheckPermissions(Permissions.AssetFile_Delete);
                if (!hasClaim) return;
            }
            catch (Exception e)
            {
                Log.Warning(e, nameof(TryDeleteAssetFile));
                return;
            }
            
            
            View?.DisplayOkCancelDialog(null, TextService.GetString("sureDeletePhoto"), okCancel =>
            {
                if (okCancel != OkCancel.Ok) return;
                
                DeleteAssetFile(assetFileIndex);
            });
        }

        public async void AddAssetFile()
        {
            var view = View;
            if (view == null)
                return;
            
            var assetFileId = Guid.NewGuid();
            string uploadPhotoPath;
            string cachePhotoPath;
            
            try
            {
                var hasClaim = await CheckPermissions(Permissions.AssetFile_Add);
                if (!hasClaim) return;
                
                var fileLocalName = AssetsHelperMethods.GetAssetFileLocalName(assetFileId);
                uploadPhotoPath = Path.Combine(MyHelperService.GetPhotoFilesToUploadDirectory(), fileLocalName);
                cachePhotoPath = AssetsHelperMethods.GetAssetFileLocalPath(PathResolver, assetFileId);
                if (view is IPhotoCaptureView photoCaptureView)
                {
                    photoCaptureView.PhotoTaken += HandlePhoto;
                    var cameraOpened = photoCaptureView.Capture(view, uploadPhotoPath);
                    if (!cameraOpened)
                        photoCaptureView.PhotoTaken -= HandlePhoto;
                }
            }
            catch (Exception e)
            {
                Log.Warning(e, nameof(AddAssetFile));
            }
            
            void HandlePhoto(object sender, bool photoTaken)
            {
                Task.Run(async () =>
                {
                    AssetFile assetFile = null;
                    try
                    {
                        if (view is IPhotoCaptureView photoCaptureView)
                            photoCaptureView.PhotoTaken -= HandlePhoto;
                    
                        if (!photoTaken)
                        {
                            if (File.Exists(uploadPhotoPath))
                                File.Delete(uploadPhotoPath);
                            return;
                        }
                        
                        using (var codec = SKCodec.Create(uploadPhotoPath))
                        {
                            const int compressedWidth = 960;
                            var realScale = (float) codec.Info.Height / codec.Info.Width;
                            var compressedHeight = (int) (realScale * compressedWidth);
                            var (canvasAction, useWidth, useHeight) = MyHelperMethods.GetOrientationAction(codec.EncodedOrigin, compressedWidth, compressedHeight);
                            var desiredInfo = new SKImageInfo(useWidth, useHeight);

                            // get the scale that is nearest to what we want (eg: jpg returned 512)
                            var supportedScale = codec.GetScaledDimensions((float) useWidth / codec.Info.Width);
                            var nearestInfo = new SKImageInfo(supportedScale.Width, supportedScale.Height);
                            // decode the bitmap at the nearest size
                            using var bmp = SKBitmap.Decode(codec, nearestInfo);
                            using var resizedBmp = bmp.Resize(desiredInfo, SKFilterQuality.Medium);
                            using var surface = SKSurface.Create(desiredInfo);
                            canvasAction?.Invoke(surface.Canvas);
                            surface.Canvas.DrawImage(SKImage.FromBitmap(resizedBmp), desiredInfo.Rect);
                            surface.Canvas.Flush();
                            using var image = surface.Snapshot();
                            var d = image.Encode(codec.EncodedFormat, 100);
                            using var uploadPhoto = File.Create(uploadPhotoPath);
                            d.SaveTo(uploadPhoto);
                            using var cachePhoto = File.Create(cachePhotoPath);
                            d.SaveTo(cachePhoto);
                        }

                        assetFile = AssetFile.Create(_assetId, assetFileId);
                        var newAssetFiles = await _assetFileRepo.GetByAssetIdAsync(_assetId);
                        _asset.AssetFiles = newAssetFiles;
                        view.AssetFileList = newAssetFiles;
                    }
                    catch (Exception e)
                    {
                        Log.Warning(e, nameof(HandlePhoto));
                        try
                        {
                            if (assetFile != null)
                                AssetFile.Delete(assetFile);
                            if (File.Exists(uploadPhotoPath))
                                File.Delete(uploadPhotoPath);
                            if (File.Exists(cachePhotoPath))
                                File.Delete(cachePhotoPath);
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, nameof(HandlePhoto));
                        }
                    }
                });
            }
        }

        private void DeleteAssetFile(int assetFileIndex)
        {
            var view = View;
            if (view == null)
                return;
            
            if (assetFileIndex < 0)
                return;

            Task.Run(async () =>
            {
                try
                {
                    var assetFile = _asset.AssetFiles[assetFileIndex];
                    if (assetFile == null)
                    {
                        Log.Warning("AssetFile is null!");
                        return;
                    }
                    
                    AssetFile.Delete(assetFile);
                    
                    var cachePath = assetFile.GetLocalPath(PathResolver);
                    if (File.Exists(cachePath))
                        File.Delete(cachePath);
                    var uploadPath = Path.Combine(MyHelperService.GetPhotoFilesToUploadDirectory(), assetFile.LocalName);
                    if (File.Exists(uploadPath))
                        File.Delete(uploadPath);

                    var newAssetFiles = await _assetFileRepo.GetByAssetIdAsync(_assetId);
                    _asset.AssetFiles = newAssetFiles;
                    view.AssetFileList = newAssetFiles;
                }
                catch (Exception e)
                {
                    Log.Warning(e, nameof(DeleteAssetFile));
                }
            });
        }
        
        private void AddAssetCode(CodeTypeEnum codeType, string code)
        {
            var view = View;
            if (view == null)
                return;
            
            if (string.IsNullOrEmpty(code) || codeType == CodeTypeEnum.Default)
            {
                Log.Warning("Code is null or codeType is CodeTypeEnum.Default!");
                return;
            }

            if (codeType == CodeTypeEnum.CameraModule)
            {
                if (!code.IsBarcodeFormat())
                {
                    view.DisplaySuccessMessage(TextService.GetString("codeDoesNotMatchFormat"));
                    return;
                }
            }

            Task.Run(async () =>
            {
                try
                {
                    var result = await _codeRepo.InsertAsync(CodeEntityEnum.Asset, codeType, _assetId, code);

                    if (result != AlterAssetError.NoError)
                    {
                        var message = TextService.GetString("errorOccured");
                        switch (result)
                        {
                            case AlterAssetError.CodeAlreadyInUse:
                                message = TextService.GetString("epcAlreadyInUse");
                                break;
                            case AlterAssetError.Exception:
                                break;
                        }

                        view.DisplayErrorMessage(message, null);
                    }
                    else
                    {
                        var newAssetCodes = await _codeRepo.GetByEntityIdAsync(CodeEntityEnum.Asset, _assetId);
                        _asset.Codes = newAssetCodes;
                        view.CodeList = newAssetCodes;
                    }
                }
                catch (Exception e)
                {
                    Log.Warning(e, nameof(AddAssetCode));
                }
            });
        }

        private void DeleteAssetCode(int codeIndex)
        {
            var view = View;
            if (view == null)
                return;
            
            if (codeIndex < 0)
                return;

            Task.Run(async () =>
            {
                try
                {
                    var code = _asset.Codes[codeIndex];
                    if (code == null)
                    {
                        Log.Error("Code is null!");
                        return;
                    }

                    Code.Delete(code);

                    var newAssetCodes = await _codeRepo.GetByEntityIdAsync(CodeEntityEnum.Asset, _assetId);
                    _asset.Codes = newAssetCodes;
                    view.CodeList = newAssetCodes;
                }
                catch (Exception e)
                {
                    Log.Warning(e, nameof(DeleteAssetCode));
                }
            });
        }
        
        public async void TryOverwriteCoordinates()
        {
            var view = View;
            if (view == null)
                return;
            
            try
            {
                var hasPermission = await CheckPermissions(Permissions.Coordinates_Edit);
                if (!hasPermission) return;

                var (latitude, longitude) = await HelperService.ShowLocationDialogAsync(view, _asset.Latitude, _asset.Longitude);
                if (!latitude.HasValue || !longitude.HasValue) return;
                
                _asset.SetCoordinates(latitude, longitude);
                view.CoordinatesExist = true;
            }
            catch (Exception e)
            {
                Log.Warning(e, nameof(TryOverwriteCoordinates));
                view.DisplayErrorMessage(TextService.GetString("errorOccured"), e.Message);
            }
        }

        public async Task<bool> DeleteCoordinatesVisibleAsync()
        {
            return await PermissionService.CheckPermissionAsync(Permissions.Coordinates_Edit.NormalizePermission())
                   && _asset.Latitude.HasValue && _asset.Longitude.HasValue;
        }

        public void TryDeleteCoordinates()
        {
            View?.DisplayOkCancelDialog(null, TextService.GetString("sureDeleteGps"), okCancel =>
            {
                if (okCancel != OkCancel.Ok) return;
                
                DeleteCoordinates();
            });
        }

        private void DeleteCoordinates()
        {
            var view = View;
            if (view == null)
                return;
            
            Task.Run(async () =>
            {
                try
                {
                    var hasPermission = await CheckPermissions(Permissions.Coordinates_Edit);
                    if (!hasPermission) return;
                    
                    view.ProgressBarVisible = false;
                    
                    _asset.SetCoordinates(null, null);
                    view.CoordinatesExist = false;
                }
                catch (Exception e)
                {
                    Log.Warning(e, nameof(DeleteCoordinates));
                    view.DisplayErrorMessage(TextService.GetString("errorOccured"), e.Message);
                }
                finally
                {
                    view.ProgressBarVisible = false;
                }
            });
        }

        public void GoToParentAssetDetails()
        {
            var view = View;
            if (view == null)
                return;
            
            if (_asset.ParentId.HasValue)
                view.GoToAssetDetails(_asset.ParentId.Value);
        }

        public void ChildAssetSelected(int index)
        {
            var view = View;
            if (view == null)
                return;
            
            if (index < 0)
            {
                Log.Warning($"Index is less than 0 at {nameof(ChildAssetSelected)}");
                return;
            }

            var childAsset = _childAssetList?.ElementAtOrDefault(index);
            if (childAsset == null)
            {
                Log.Warning($"No child asset at index {index} at {nameof(ChildAssetSelected)}");
                return;
            }
            
            view.GoToAssetDetails(childAsset.Id);
        }
    }
}
