-- Demo data for local runs and tests (Sqlite). Mirrors samples/sqlserver/seed.sql.
CREATE TABLE Regions (
  Id INTEGER PRIMARY KEY,
  Name TEXT NOT NULL
);

CREATE TABLE Customers (
  Id INTEGER PRIMARY KEY,
  Name TEXT NOT NULL,
  City TEXT NOT NULL,
  RegionId INTEGER NOT NULL REFERENCES Regions (Id),
  Email TEXT NULL,
  CreatedOn DATE NOT NULL
);

CREATE TABLE Orders (
  Id INTEGER PRIMARY KEY,
  OrderNumber TEXT NOT NULL,
  OrderDate DATE NOT NULL,
  CustomerId INTEGER NOT NULL REFERENCES Customers (Id),
  Quantity INTEGER NOT NULL,
  Total DECIMAL(18, 2) NOT NULL
);

INSERT INTO Regions (Id, Name) VALUES
  (1, 'North'), (2, 'South'), (3, 'East'), (4, 'West');

INSERT INTO Customers (Id, Name, City, RegionId, Email, CreatedOn) VALUES
  (1, 'Aurora Trading', 'Medan', 1, 'orders@aurora.example', '2023-02-14'),
  (2, 'Bintang Logistics', 'Surabaya', 3, 'ap@bintang.example', '2022-11-03'),
  (3, 'Cahaya Foods', 'Bandung', 4, NULL, '2024-05-20'),
  (4, 'Delta Hardware', 'Makassar', 3, 'buy@delta.example', '2021-08-09'),
  (5, 'Elang Pharmacy', 'Denpasar', 2, 'finance@elang.example', '2024-01-30'),
  (6, 'Fajar Textiles', 'Semarang', 4, 'po@fajar.example', '2023-07-12'),
  (7, 'Garuda Motors', 'Palembang', 1, 'parts@garuda.example', '2020-03-18'),
  (8, 'Harapan Clinic', 'Yogyakarta', 2, NULL, '2025-02-01'),
  (9, 'Indah Furniture', 'Pontianak', 4, 'sales@indah.example', '2022-04-25'),
  (10, 'Jaya Construction', 'Balikpapan', 3, 'procure@jaya.example', '2021-12-07'),
  (11, 'Kencana Retail', 'Malang', 3, 'buyer@kencana.example', '2025-06-16'),
  (12, 'Lestari Agro', 'Pekanbaru', 1, 'office@lestari.example', '2023-10-10');

-- 240 orders spread over January to September 2026.
WITH RECURSIVE n(i) AS (
  SELECT 1
  UNION ALL
  SELECT i + 1 FROM n WHERE i < 240
)
INSERT INTO Orders (Id, OrderNumber, OrderDate, CustomerId, Quantity, Total)
SELECT
  i,
  printf('SO-%05d', 10000 + i),
  date('2026-01-01', '+' || ((i * 37) % 270) || ' days'),
  ((i * 7) % 12) + 1,
  ((i * 11) % 40) + 1,
  round((((i * 11) % 40) + 1) * (15.75 + ((i * 13) % 60) * 2.5), 2)
FROM n;
