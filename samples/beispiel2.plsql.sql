-- ============================================================================
-- Konvertiert von T-SQL nach PL/SQL (Ziel: Oracle 19c)
-- Automatische Umsetzung – Ergebnis prüfen, Stellen mit TODO manuell nacharbeiten.
--
-- Hinweise:
--   * Leerstring '' ist in Oracle NULL – Vergleiche wie = '' bzw. <> '' prüfen (IS NULL).
--   * Temporäre Tabellen (#tmp) → Global Temporary Tables 'tmp_…' (einmalig per DDL anlegen, nicht im Code).
--   * CREATE TABLE #tmp → CREATE GLOBAL TEMPORARY TABLE (gehört in ein einmaliges DDL-Skript).
--   * UPDATE ... FROM ... JOIN → MERGE umgesetzt: Join-Spalten dürfen nicht aktualisiert werden (ORA-38104), die Quelle muss je Zielzeile eindeutig sein (ORA-30926).
--   * MERGE: In Oracle muss jede WHEN-Klausel eindeutig sein; 'WHEN MATCHED AND ...' → WHERE-Klausel im UPDATE.
--   * Ergebnismengen (SELECT ohne INTO) → OPEN cursor FOR ... + DBMS_SQL.RETURN_RESULT (ab Oracle 12c).
--   * sp_executesql → EXECUTE IMMEDIATE ... USING: dynamisches SQL auf Oracle-Syntax prüfen.
--   * SELECT @s = @s + spalte FROM ... (String-Aggregation) → LISTAGG (max. 4000 Byte, Reihenfolge per WITHIN GROUP festlegen).
--   * ALTER COLUMN → MODIFY: NOT NULL auf bereits NOT-NULL-Spalte erzeugt ORA-01442.
--   * Bezeichner 'Level' ist in Oracle reserviert → als "LEVEL" gequotet.
--   * Inline-Tabellenwertfunktion fn_KundenAb → Funktion mit SYS_REFCURSOR (alternativ View, SQL-Macro oder PIPELINED).
-- ============================================================================

-- ----------------------------------------------------------------------------
-- Einmalig anzulegende Objekte (Global Temporary Tables für #temp/@tabellen)
-- ----------------------------------------------------------------------------
CREATE GLOBAL TEMPORARY TABLE tmp_Offen (
    Id NUMBER(10) PRIMARY KEY,
    Betrag NUMBER(19,4),
    Faellig TIMESTAMP(0)
) ON COMMIT PRESERVE ROWS;

-- ----------------------------------------------------------------------------

DECLARE
    v_Stichtag DATE := TO_DATE('2024-12-31', 'YYYY-MM-DD');
    v_Anzahl NUMBER(10);
    v_Text NVARCHAR2(16383) := N'';
    v_sub1 NUMBER;
    v_exists2 NUMBER(1);
    c_result3 SYS_REFCURSOR;
    v_sql NVARCHAR2(4000);
    c_result4 SYS_REFCURSOR;
BEGIN
    DELETE FROM tmp_Offen; -- DROP #temp → GTT leeren
    DELETE FROM tmp_Offen; -- #temp-Tabelle → GTT (siehe Vorab-DDL)
    INSERT INTO tmp_Offen (Id, Betrag, Faellig)
    SELECT r.RechnungId, r.Betrag, r.Faellig
    FROM Rechnung r
    WHERE r.Bezahlt = 0 AND r.Faellig < v_Stichtag;
    SELECT COUNT(*) INTO v_Anzahl FROM tmp_Offen;
    SELECT SUM(Betrag) INTO v_sub1 FROM tmp_Offen;
    IF v_sub1 > 10000 THEN
        DBMS_OUTPUT.PUT_LINE('Hohe Außenstände: ' || CAST(v_Anzahl AS VARCHAR2(20)));
    ELSIF v_Anzahl = 0 THEN
        DBMS_OUTPUT.PUT_LINE('Keine offenen Posten');
    ELSE
        MERGE INTO Rechnung r
        USING tmp_Offen o
        ON (o.Id = r.RechnungId)
        WHEN MATCHED THEN UPDATE SET r.Mahnstufe = r.Mahnstufe + 1, r.GeaendertAm = SYSTIMESTAMP;
    END IF;
    SELECT CASE WHEN EXISTS (
        SELECT 1 FROM tmp_Offen WHERE Betrag < 0
    ) THEN 1 ELSE 0 END INTO v_exists2 FROM DUAL;
    WHILE v_exists2 = 1 LOOP
        DELETE FROM tmp_Offen WHERE (Betrag < 0) AND ROWNUM <= 100;
        IF SQL%ROWCOUNT = 0 THEN
            EXIT;
        END IF;
        -- Bedingung neu auswerten
        SELECT CASE WHEN EXISTS (
            SELECT 1 FROM tmp_Offen WHERE Betrag < 0
        ) THEN 1 ELSE 0 END INTO v_exists2 FROM DUAL;
    END LOOP;
    MERGE INTO Kunde ziel
    USING KundeImport quelle ON ziel.KundeNr = quelle.KundeNr
    WHEN MATCHED THEN UPDATE SET ziel.Name = quelle.Name
    WHEN NOT MATCHED THEN INSERT (KundeNr, Name) VALUES (quelle.KundeNr, quelle.Name);
    OPEN c_result3 FOR
        WITH Umsatz AS (
            SELECT KundeId, SUM(Betrag) AS Summe, ROW_NUMBER() OVER (ORDER BY SUM(Betrag) DESC) AS Rang
            FROM Rechnung
            WHERE EXTRACT(YEAR FROM Datum) = EXTRACT(YEAR FROM SYSDATE) AND Kommentar <> ''
            GROUP BY KundeId
        )
        SELECT u.KundeId, u.Summe, LISTAGG(k.Name, ', ') WITHIN GROUP (ORDER BY NULL) AS Namen, CASE WHEN u.Summe > 1000 THEN 'A' ELSE 'B' END AS Klasse
        FROM Umsatz u JOIN Kunde k ON k.KundeId = u.KundeId
        WHERE u.Rang <= 10
        GROUP BY u.KundeId, u.Summe;
    DBMS_SQL.RETURN_RESULT(c_result3);
    v_sql := N'SELECT * FROM dbo.Kunde WHERE KundeId = @id';
    EXECUTE IMMEDIATE 'SELECT COUNT(*) FROM Kunde WHERE KundeId = :id' USING 5;
    usp_KundeAnlegen(p_Name => N'Test', p_NeueId => v_Anzahl);
    SELECT v_Text || LISTAGG(Name || ';') WITHIN GROUP (ORDER BY NULL) INTO v_Text FROM Kunde;
    OPEN c_result4 FOR
        SELECT INSTR(v_Text, 'x'), SUBSTR(v_Text, 1, 5), SUBSTR(v_Text, -3), RPAD('-', LENGTH('-') * 10, '-'), TO_CHAR(SYSDATE, 'DD.MM.YYYY HH24:MI') FROM DUAL;
    DBMS_SQL.RETURN_RESULT(c_result4);
END;
/

BEGIN
    EXECUTE IMMEDIATE 'DROP TABLE Archiv';
EXCEPTION
    WHEN OTHERS THEN
        IF SQLCODE != -942 THEN RAISE; END IF;
END;
/

ALTER TABLE Kunde ADD (Telefon VARCHAR2(30) NULL, Geburtstag DATE NULL);

ALTER TABLE Kunde MODIFY (Email NVARCHAR2(320) NOT NULL);

ALTER TABLE Kunde MODIFY ("LEVEL" DEFAULT 1);

ALTER TABLE Bestellung ADD CONSTRAINT FK_Best_Kunde FOREIGN KEY (KundeId) REFERENCES Kunde (KundeId) ON DELETE CASCADE;

CREATE OR REPLACE VIEW vAktiveKunden AS
SELECT KundeId, Name, TRUNC(ErstelltAm) AS Tag
FROM Kunde WHERE Aktiv = 1;

-- TODO: Inline-Tabellenwertfunktion – als View, SQL-Macro (21c+) oder PIPELINED Function erwägen
CREATE OR REPLACE FUNCTION fn_KundenAb (
    p_Ab IN DATE
)
RETURN SYS_REFCURSOR
AS
    c_result SYS_REFCURSOR;
BEGIN
    OPEN c_result FOR
        SELECT KundeId, Name FROM Kunde WHERE ErstelltAm >= p_Ab;
    RETURN c_result;
END fn_KundenAb;
/
