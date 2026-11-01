-- Schema change for databases created by an earlier Helio.NET build (EnsureCreated does not migrate).
-- Adds persisted chunk embeddings and drops the low-cardinality index on query_records.Abstained.
-- Idempotent; run once per environment before deploying the new image. SQL Server syntax.
IF COL_LENGTH('chunks', 'EmbeddingModel') IS NULL
    ALTER TABLE chunks ADD EmbeddingModel NVARCHAR(100) NULL;
IF COL_LENGTH('chunks', 'Vector') IS NULL
    ALTER TABLE chunks ADD Vector VARBINARY(MAX) NULL;
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_query_records_Abstained' AND object_id = OBJECT_ID('query_records'))
    DROP INDEX IX_query_records_Abstained ON query_records;
-- SQLite equivalent (development): delete data/helio.db and let the app recreate it, or run:
--   ALTER TABLE chunks ADD COLUMN EmbeddingModel TEXT NULL; ALTER TABLE chunks ADD COLUMN Vector BLOB NULL;
--   DROP INDEX IF EXISTS IX_query_records_Abstained;
