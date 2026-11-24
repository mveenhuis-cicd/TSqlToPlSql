DECLARE @Stichtag DATE = '2024-12-31';
DECLARE @Anzahl INT, @Text NVARCHAR(MAX) = N'';

IF OBJECT_ID('tempdb..#Offen') IS NOT NULL DROP TABLE #Offen;

CREATE TABLE #Offen (Id INT PRIMARY KEY, Betrag MONEY, Faellig DATETIME2(0));

INSERT INTO #Offen (Id, Betrag, Faellig)
SELECT r.RechnungId, r.Betrag, r.Faellig
FROM dbo.Rechnung r
WHERE r.Bezahlt = 0 AND r.Faellig < @Stichtag;

SET @Anzahl = (SELECT COUNT(*) FROM #Offen);

IF (SELECT SUM(Betrag) FROM #Offen) > 10000
    PRINT 'Hohe Außenstände: ' + CAST(@Anzahl AS VARCHAR(20));
ELSE IF @Anzahl = 0
    PRINT 'Keine offenen Posten';
ELSE
BEGIN
    UPDATE r SET r.Mahnstufe = r.Mahnstufe + 1, r.GeaendertAm = SYSDATETIME()
    FROM dbo.Rechnung r INNER JOIN #Offen o ON o.Id = r.RechnungId;
END

WHILE EXISTS (SELECT 1 FROM #Offen WHERE Betrag < 0)
BEGIN
    DELETE TOP (100) FROM #Offen WHERE Betrag < 0;
    IF @@ROWCOUNT = 0 BREAK;
END

MERGE INTO dbo.Kunde AS ziel
USING dbo.KundeImport AS quelle ON ziel.KundeNr = quelle.KundeNr
WHEN MATCHED THEN UPDATE SET ziel.Name = quelle.Name
WHEN NOT MATCHED BY TARGET THEN INSERT (KundeNr, Name) VALUES (quelle.KundeNr, quelle.Name);

;WITH Umsatz AS (
    SELECT KundeId, SUM(Betrag) AS Summe, ROW_NUMBER() OVER (ORDER BY SUM(Betrag) DESC) AS Rang
    FROM dbo.Rechnung
    WHERE YEAR(Datum) = YEAR(GETDATE()) AND Kommentar <> ''
    GROUP BY KundeId
)
SELECT u.KundeId, u.Summe, STRING_AGG(k.Name, ', ') AS Namen, IIF(u.Summe > 1000, 'A', 'B') AS Klasse
FROM Umsatz u JOIN dbo.Kunde k ON k.KundeId = u.KundeId
WHERE u.Rang <= 10
GROUP BY u.KundeId, u.Summe;

DECLARE @sql NVARCHAR(4000) = N'SELECT * FROM dbo.Kunde WHERE KundeId = @id';
EXEC sp_executesql N'SELECT COUNT(*) FROM dbo.Kunde WHERE KundeId = @id', N'@id INT', @id = 5;
EXEC dbo.usp_KundeAnlegen @Name = N'Test', @NeueId = @Anzahl OUTPUT;

SELECT @Text = @Text + Name + ';' FROM dbo.Kunde;
SELECT CHARINDEX('x', @Text), LEFT(@Text, 5), RIGHT(@Text, 3), REPLICATE('-', 10), FORMAT(GETDATE(), 'dd.MM.yyyy HH:mm');
GO

DROP TABLE IF EXISTS dbo.Archiv;
GO

ALTER TABLE dbo.Kunde ADD Telefon VARCHAR(30) NULL, Geburtstag DATE NULL;
ALTER TABLE dbo.Kunde ALTER COLUMN Email NVARCHAR(320) NOT NULL;
ALTER TABLE dbo.Kunde ADD CONSTRAINT DF_Kunde_Level DEFAULT 1 FOR [Level];
ALTER TABLE dbo.Bestellung ADD CONSTRAINT FK_Best_Kunde FOREIGN KEY (KundeId) REFERENCES dbo.Kunde (KundeId) ON DELETE CASCADE;
GO

CREATE VIEW dbo.vAktiveKunden
WITH SCHEMABINDING
AS
SELECT TOP 100 PERCENT KundeId, Name, CONVERT(DATE, ErstelltAm) AS Tag
FROM dbo.Kunde WHERE Aktiv = 1
GO

CREATE FUNCTION dbo.fn_KundenAb (@Ab DATE)
RETURNS TABLE
AS
RETURN (SELECT KundeId, Name FROM dbo.Kunde WHERE ErstelltAm >= @Ab);
GO
