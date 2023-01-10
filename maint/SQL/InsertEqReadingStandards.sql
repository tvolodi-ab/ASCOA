insert into infrastructure.EqReadingsStandards 
	(AssetClassId, ReadingTypeId, ComparisonType, MaxAlertValue, `MaxValue`, MinAlertValue, `MinValue`)
select *, 
	'MoreThan' as ComparisonType,
    800 as MaxAlertValue,
    1000 as "MaxValue",
    0 as MinAlertValue,
    0 as MinValue
	from 
    (
		select ac.Id as AssetClassId
		from inventory.AssetClasses ac
		where ac.Description = 'Вагоны'
	)  as AssetClassId,	
	(select rt.Id as ReadingTypeId
		from infrastructure.ReadingTypes rt 
        where rt.Description = 'Пробег'
	) as ReadingTypeId;