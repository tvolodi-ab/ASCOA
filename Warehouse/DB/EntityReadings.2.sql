SELECT count(*) FROM infrastructure.EntityReadings;

select 
	-- ac.Description
    a.Description,
    er.Value
	from inventory.Assets a
		join inventory.AssetClasses ac on a.AssetClassId = ac.Id
        join infrastructure.EntityReadings er on er.EntityGuidId = a.Id
	-- group by ac.Id
;    


select count(*) from 
(
	select distinct a.Description asset_descr,  ac.Description class_descr,
		max(cast(er.Value as decimal)) over(partition by a.Id) as max_by_asset,
		avg(er.Value) over(partition by ac.Id) as avg_by_class
		from infrastructure.EntityReadings er    
		join inventory.Assets a on a.Id = er.EntityGuidId
		join inventory.AssetClasses ac on ac.Id = a.AssetClassId
) mileage
;

-- Car mileage with average of their class
select 
	a.Description asset_descr,  ac.Description class_descr,
    vals.value_dec,
    avg(vals.value_dec) over(partition by ac.Id) as avg_by_class
	from inventory.Assets a
		join inventory.AssetClasses ac on ac.Id = a.AssetClassId
        join 
        (
			select er.EntityGuidId asset_id, 
				max(cast(er.Value as decimal)) value_dec 
				from infrastructure.EntityReadings er
                where er.IsRemoved = 0
				group by er.EntityGuidId
                
		) vals on vals.asset_id = a.Id
        ;
        
--         
	select * 
		from inventory.Assets a
			join inventory.AssetClasses ac on ac.Id = a.AssetClassId
				select er.EntityGuidId asset_id, 
				max(cast(er.Value as decimal)) value_dec 
				from infrastructure.EntityReadings er
				group by er.EntityGuidId