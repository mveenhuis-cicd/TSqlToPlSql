-- Kundentabelle
CREATE TABLE [dbo].[Kunde] (
    [KundeId]     INT IDENTITY(1,1) NOT NULL,
    [Name]        NVARCHAR(100) NOT NULL,
    [Email]       VARCHAR(255) NULL,
    [Level]       TINYINT NOT NULL DEFAULT 0,
    [Aktiv]       BIT NOT NULL CONSTRAINT DF_Kunde_Aktiv DEFAULT (1),
    [Notiz]       NVARCHAR(MAX) NULL,
    [ErstelltAm]  DATETIME NOT NULL DEFAULT GETDATE(),
    CONSTRAINT [PK_Kunde] PRIMARY KEY CLUSTERED ([KundeId] ASC)
) ON [PRIMARY]
GO

CREATE NONCLUSTERED INDEX IX_Kunde_Name ON dbo.Kunde (Name) INCLUDE (Email) WITH (FILLFACTOR = 90)
GO

INSERT INTO dbo.Kunde (Name, Email) VALUES (N'Müller', 'm@x.de'), (N'Meier', NULL)
GO

SELECT TOP 10 KundeId, Anzeige = Name + ' <' + ISNULL(Email, '-') + '>', DATEDIFF(day, ErstelltAm, GETDATE()) AS Tage
FROM dbo.Kunde k WITH (NOLOCK)
WHERE LEN(Name) > 3 AND ErstelltAm >= DATEADD(month, -6, GETDATE())
ORDER BY Name
GO

CREATE PROCEDURE dbo.usp_KundeAnlegen
    @Name NVARCHAR(100),
    @Email VARCHAR(255) = NULL,
    @NeueId INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Anzahl INT = 0, @Msg VARCHAR(200);

    IF EXISTS (SELECT 1 FROM dbo.Kunde WHERE Email = @Email)
    BEGIN
        RAISERROR('Kunde mit E-Mail %s existiert bereits', 16, 1, @Email);
        RETURN -1;
    END

    BEGIN TRY
        BEGIN TRANSACTION;
        INSERT INTO dbo.Kunde (Name, Email) VALUES (@Name, @Email);
        SET @NeueId = SCOPE_IDENTITY();
        SELECT @Anzahl = COUNT(*) FROM dbo.Kunde;
        SET @Msg = 'Kunde ' + CAST(@NeueId AS VARCHAR(10)) + ' angelegt, gesamt: ' + CONVERT(VARCHAR(10), @Anzahl);
        PRINT @Msg;
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        INSERT INTO dbo.FehlerLog (Meldung, Zeit) VALUES (ERROR_MESSAGE(), GETDATE());
        THROW;
    END CATCH
END
GO

CREATE FUNCTION dbo.fn_Alter (@Geburt DATE)
RETURNS INT
AS
BEGIN
    DECLARE @a INT;
    SET @a = DATEDIFF(year, @Geburt, GETDATE());
    IF DATEADD(year, @a, @Geburt) > GETDATE() SET @a = @a - 1;
    RETURN @a;
END
GO

CREATE PROCEDURE dbo.usp_Report @Ab DATETIME
AS
    DECLARE @Id INT, @Name NVARCHAR(100), @i INT = 0;
    DECLARE cur CURSOR LOCAL FAST_FORWARD FOR
        SELECT KundeId, Name FROM dbo.Kunde WHERE ErstelltAm >= @Ab;
    OPEN cur;
    FETCH NEXT FROM cur INTO @Id, @Name;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @i += 1;
        IF @i % 100 = 0 PRINT 'Zeile ' + CAST(@i AS VARCHAR(10));
        ELSE IF @Name LIKE 'A%' PRINT @Name;
        ELSE PRINT CONVERT(VARCHAR(10), @Ab, 104);
        FETCH NEXT FROM cur INTO @Id, @Name;
    END
    CLOSE cur;
    DEALLOCATE cur;

    SELECT k.KundeId, k.Name FROM dbo.Kunde AS k INNER JOIN dbo.Bestellung AS b ON b.KundeId = k.KundeId
    WHERE b.Datum BETWEEN @Ab AND GETDATE();
GO
