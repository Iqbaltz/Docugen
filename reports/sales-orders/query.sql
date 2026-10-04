SELECT
    o.OrderDate,
    o.OrderNumber,
    c.Name AS Customer,
    r.Name AS Region,
    o.Quantity,
    o.Total
FROM Orders o
JOIN Customers c ON c.Id = o.CustomerId
JOIN Regions r ON r.Id = c.RegionId
WHERE o.OrderDate >= @FromDate
  AND o.OrderDate <= @ToDate
  AND (@Region IS NULL OR r.Name = @Region)
ORDER BY o.OrderDate, o.OrderNumber
