-- Demo schema and data for SQL Server (local development on LocalDB and the integration tests). Mirrors samples/sqlite/seed.sql.
CREATE TABLE dbo.Regions (
  Id INT NOT NULL PRIMARY KEY,
  Name NVARCHAR(50) NOT NULL
);

CREATE TABLE dbo.Customers (
  Id INT NOT NULL PRIMARY KEY,
  Name NVARCHAR(100) NOT NULL,
  City NVARCHAR(100) NOT NULL,
  RegionId INT NOT NULL REFERENCES dbo.Regions (Id),
  Email NVARCHAR(200) NULL,
  CreatedOn DATE NOT NULL
);

CREATE TABLE dbo.Orders (
  Id INT NOT NULL PRIMARY KEY,
  OrderNumber NVARCHAR(20) NOT NULL,
  OrderDate DATE NOT NULL,
  CustomerId INT NOT NULL REFERENCES dbo.Customers (Id),
  Quantity INT NOT NULL,
  Total DECIMAL(18, 2) NOT NULL
);

INSERT INTO dbo.Regions (Id, Name) VALUES
  (1, N'North'), (2, N'South'), (3, N'East'), (4, N'West');

INSERT INTO dbo.Customers (Id, Name, City, RegionId, Email, CreatedOn) VALUES
  (1, N'Aurora Trading', N'Medan', 1, N'orders@aurora.example', '2023-02-14'),
  (2, N'Bintang Logistics', N'Surabaya', 3, N'ap@bintang.example', '2022-11-03'),
  (3, N'Cahaya Foods', N'Bandung', 4, NULL, '2024-05-20'),
  (4, N'Delta Hardware', N'Makassar', 3, N'buy@delta.example', '2021-08-09'),
  (5, N'Elang Pharmacy', N'Denpasar', 2, N'finance@elang.example', '2024-01-30'),
  (6, N'Fajar Textiles', N'Semarang', 4, N'po@fajar.example', '2023-07-12'),
  (7, N'Garuda Motors', N'Palembang', 1, N'parts@garuda.example', '2020-03-18'),
  (8, N'Harapan Clinic', N'Yogyakarta', 2, NULL, '2025-02-01'),
  (9, N'Indah Furniture', N'Pontianak', 4, N'sales@indah.example', '2022-04-25'),
  (10, N'Jaya Construction', N'Balikpapan', 3, N'procure@jaya.example', '2021-12-07'),
  (11, N'Kencana Retail', N'Malang', 3, N'buyer@kencana.example', '2025-06-16'),
  (12, N'Lestari Agro', N'Pekanbaru', 1, N'office@lestari.example', '2023-10-10');

WITH n AS (
  SELECT TOP (240) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i
  FROM sys.all_objects
)
INSERT INTO dbo.Orders (Id, OrderNumber, OrderDate, CustomerId, Quantity, Total)
SELECT
  i,
  CONCAT('SO-', FORMAT(10000 + i, '00000')),
  DATEADD(DAY, (i * 37) % 270, CAST('2026-01-01' AS DATE)),
  ((i * 7) % 12) + 1,
  ((i * 11) % 40) + 1,
  ROUND((((i * 11) % 40) + 1) * (15.75 + ((i * 13) % 60) * 2.5), 2)
FROM n;
