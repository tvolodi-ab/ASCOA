SELECT * FROM infrastructure.EqMaintReadings;

insert into infrastructure.EqMaintReadings 
	(Id, AssetId, TypeOfMaintenanceId, EqMaintJournalId, ReadingTypeId, ReadingValue, OccuredAt) values
    (	uuid_to_bin(uuid()), 
		(select Id from inventory.Assets where Name = 'Думпкар 563'),
        (select Id from infrastructure.TypesOfMaintenance where Description = 'ТО1'),
        (select Id from infrastructure.EqMaintJournal limit 1), -- fiction id
        (select Id from infrastructure.ReadingTypes where Code = 'MILEAGE'),
        1050,
        now() - 4
	);
    
select * from infrastructure.EntityReadings er 
	join inventory.Assets a on a.Id = er.EntityGuidId
	where a.Name = 'Думпкар 563';
    
insert into infrastructure.EntityReadings
	(Id, ReadingTypeId, EntityType, EntityGuidId, Value, OccuredAt)
    values
    (
		uuid_to_bin(uuid())
        , (select Id from infrastructure.ReadingTypes where Code = 'MILEAGE')
        , 'Assets'
        , (select Id from inventory.Assets where Name = 'Думпкар 421')
        , 1011
        , '2023-05-27T05:42:54.000000'
    ); 
    
select cvh.Description comp_veh_descr
	, a.Name asset_name
    , emr.ReadingValue
    , tom.Description maint_descr
    , (select sw.Description from infrastructure.ScheduledWorks sw where sw.TypeOfMaintenanceId = tom.Id and a.AssetClassId = sw.AssetClassId limit 1) sched_work_descr
    -- , sw.Description sched_work_descr
    , (select sw.ReadingsValue from infrastructure.ScheduledWorks sw where sw.TypeOfMaintenanceId = tom.Id and a.AssetClassId = sw.AssetClassId limit 1) maint_std_val
    -- , sw.ReadingsValue maint_std_val
    , emr.OccuredAt
    , (select er.Id from infrastructure.EntityReadings er where er.OccuredAt = 
		(select max(er2.Value) from infrastructure.EntityReadings er2 
			where er2.EntityGuidId = a.Id 
				and er2.ReadingTypeId = emr.ReadingTypeId)) last_readings      
	, emj.MaintDoneDateTime
    from tracking.ComposedVehicles cvh
		join tracking.ComposedVehicleItems cvh_i on cvh_i.ComposedVehicleId = cvh.Id
		join inventory.Assets a on a.Id = cvh_i.EntityGuidId
		left join infrastructure.EqMaintReadings emr on emr.AssetId = a.Id
		join infrastructure.TypesOfMaintenance tom on tom.Id = emr.TypeOfMaintenanceId
		-- left join infrastructure.ScheduledWorks sw on sw.TypeOfMaintenanceId = tom.Id and a.AssetClassId = sw.AssetClassId
        join infrastructure.EqMaintJournal emj on emj.Id = emr.EqMaintJournalId
    ;
    
    select * from infrastructure.MaintRequestRegistries req 
		join infrastructure.
		where req.
    

