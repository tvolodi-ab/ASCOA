select avg(Value), rt.Description, ac.Description from inventory.Assets a
	join inventory.AssetClasses ac on a.AssetClassId = ac.Id
    join infrastructure.EntityReadings er on er.EntityGuidId = a.Id
    join infrastructure.ReadingTypes rt on rt.Id = er.ReadingTypeId
group by rt.Id, ac.Id
    ;
    
select a.Description, bin_to_uuid(a.Id), max(er.Value),
	avg(Value) over(partition by ac.Id) as ac_value, 
    rt.Description, ac.Description from inventory.Assets a
	join inventory.AssetClasses ac on a.AssetClassId = ac.Id
    join infrastructure.EntityReadings er on er.EntityGuidId = a.Id
    join infrastructure.ReadingTypes rt on rt.Id = er.ReadingTypeId
group by er.EntityGuidId
-- rt.Id, ac.Id
    ;
    
select a.Description, -- max(er.Value),
	avg(er.Value) over(partition by ac.Id) as avg_by_class,
    max(er.Value) over(partition by a.Id) as max_asset
	from infrastructure.EntityReadings er
    join inventory.Assets a on a.Id = er.EntityGuidId
    join inventory.AssetClasses ac on ac.Id = a.AssetClassId
    group by er.EntityGuidId;
    

    
select distinct a.Description,	
	max(er.Value) over(partition by a.Id) as max_by_asset,
    avg(er.Value) over(partition by ac.Id) as avg_by_class
	from infrastructure.EntityReadings er    
    join inventory.Assets a on a.Id = er.EntityGuidId
    join inventory.AssetClasses ac on ac.Id = a.AssetClassId;
    
select er.ReadingTypeId from infrastructure.EntityReadings er;
    