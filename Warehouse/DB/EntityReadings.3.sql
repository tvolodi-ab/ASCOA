SELECT EntityGuidId, MAX(Value) FROM infrastructure.EntityReadings er
where er.IsRemoved = 0
group by er.EntityGuidId
LIMIT 50 ;

with 
last_value_rnk_cte
as ( select  er.EntityGuidId, 
			cast(er.value as decimal) val,
            OccuredAt,
			rank() over(partition by er.EntityGuidId order by er.OccuredAt desc) rnk
		from infrastructure.EntityReadings er
        where IsRemoved = 0
        ),
lv
as ( select *
	from last_value_rnk_cte
    where rnk = 1 
    
)
select lv.EntityGuidId AssetId,
	lv.val,
    lv.OccuredAt,
    a.Description asset,
    ac.Description asset_class
	from lv
    join inventory.Assets a on a.Id = lv.EntityGuidId
    join inventory.AssetClasses ac on ac.Id = a.AssetClassId
		;
    
select * from infrastructure.EntityReadings 
	order by OccuredAt desc;