SELECT
    c.Name AS [Customer],
    c.City,
    r.Name AS [Region],
    c.Email,
    c.CreatedOn AS [Customer Since]
FROM Customers c
JOIN Regions r ON r.Id = c.RegionId
ORDER BY c.Name
