using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TSqlToPlSql.Desktop;

public partial class MainWindow : Window
{
    readonly DispatcherTimer debounce;
    string? currentFile;

    public MainWindow()
    {
        InitializeComponent();

        // Beim Tippen erst nach kurzer Pause konvertieren
        debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        debounce.Tick += (_, _) =>
        {
            debounce.Stop();
            RunConversion();
        };

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F5)
            {
                RunConversion();
                e.Handled = true;
            }
        };

        // Kommandozeile: TSqlToPlSql.Desktop.exe datei.sql
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1])) LoadFile(args[1]);
        else InputBox.Text = SampleSql;

        InputBox.Focus();
    }

    // ------------------------------------------------------------------ Konvertierung

    ConversionOptions CurrentOptions() => new()
    {
        Target = TargetBox.SelectedIndex == 1 ? OracleVersion.Oracle23ai : OracleVersion.Oracle19c,
        RemoveDboSchema = RemoveDbo.IsChecked == true,
        IncludeHeader = Header.IsChecked == true
    };

    void RunConversion()
    {
        // Steuerelemente sind während InitializeComponent evtl. noch nicht alle vorhanden
        if (InputBox == null || OutputBox == null || WarningsList == null || StatusText == null ||
            TargetBox == null || RemoveDbo == null || Header == null || WarningsHeader == null) return;

        var sw = Stopwatch.StartNew();
        try
        {
            var result = new TSqlToPlSqlConverter(CurrentOptions()).Convert(InputBox.Text);
            sw.Stop();

            OutputBox.Text = result.Output;
            WarningsList.Items.Clear();
            foreach (var w in result.Warnings) WarningsList.Items.Add(w);

            int todos = CountOccurrences(result.Output, "TODO");
            WarningsHeader.Text = $"Hinweise ({result.Warnings.Count})";
            StatusText.Text = $"Konvertiert in {sw.ElapsedMilliseconds} ms – {result.Warnings.Count} Hinweise, {todos} TODO-Stellen";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Fehler: " + ex.Message;
        }
    }

    static int CountOccurrences(string text, string word)
    {
        int count = 0, idx = 0;
        while ((idx = text.IndexOf(word, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += word.Length;
        }
        return count;
    }

    void InputBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (AutoConvert?.IsChecked != true) return;
        debounce.Stop();
        debounce.Start();
    }

    void Option_Changed(object sender, RoutedEventArgs e) => RunConversion();

    void Convert_Click(object sender, RoutedEventArgs e) => RunConversion();

    // ------------------------------------------------------------------ Dateien

    void New_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        currentFile = null;
        FileText.Text = "";
        InputBox.Clear();
        OutputBox.Clear();
        WarningsList.Items.Clear();
        WarningsHeader.Text = "Hinweise";
        StatusText.Text = "Bereit";
    }

    void Open_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "T-SQL-Datei öffnen",
            Filter = "SQL-Dateien (*.sql)|*.sql|Alle Dateien (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) == true) LoadFile(dlg.FileName);
    }

    void LoadFile(string path)
    {
        try
        {
            // SQL-Server-Skripte sind oft UTF-16 (SSMS) oder ANSI – BOM-Erkennung, sonst Windows-1252
            InputBox.Text = ReadSqlFile(path);
            currentFile = path;
            FileText.Text = path;
            RunConversion();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Datei konnte nicht gelesen werden", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    static string ReadSqlFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        bool hasBom = bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)) ||
                      bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        if (hasBom) return File.ReadAllText(path);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252).GetString(bytes);
        }
    }

    void SaveAs_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(OutputBox.Text)) RunConversion();
        var dlg = new SaveFileDialog
        {
            Title = "PL/SQL speichern",
            Filter = "SQL-Dateien (*.sql)|*.sql|Alle Dateien (*.*)|*.*",
            FileName = currentFile != null ? Path.GetFileNameWithoutExtension(currentFile) + ".plsql.sql" : "konvertiert.plsql.sql",
            InitialDirectory = currentFile != null ? Path.GetDirectoryName(currentFile) : null
        };
        if (dlg.ShowDialog(this) != true) return;
        File.WriteAllText(dlg.FileName, OutputBox.Text, new UTF8Encoding(false));
        StatusText.Text = "Gespeichert: " + dlg.FileName;
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(OutputBox.Text)) return;
        Clipboard.SetText(OutputBox.Text);
        StatusText.Text = "Ergebnis in die Zwischenablage kopiert";
    }

    void ConvertFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Ordner mit T-SQL-Dateien wählen" };
        if (dlg.ShowDialog(this) != true) return;
        var folder = dlg.FolderName;
        var files = Directory.GetFiles(folder, "*.sql", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".plsql.sql", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (files.Count == 0)
        {
            MessageBox.Show(this, "Keine .sql-Dateien gefunden.", "Ordner konvertieren");
            return;
        }

        var outDir = Path.Combine(folder, "oracle");
        Directory.CreateDirectory(outDir);
        var options = CurrentOptions();
        int warnings = 0, failed = 0;
        foreach (var f in files)
        {
            try
            {
                var r = new TSqlToPlSqlConverter(options).Convert(ReadSqlFile(f));
                var rel = Path.GetRelativePath(folder, f);
                var target = Path.Combine(outDir, Path.ChangeExtension(rel, ".plsql.sql"));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, r.Output, new UTF8Encoding(false));
                warnings += r.Warnings.Count;
            }
            catch
            {
                failed++;
            }
        }
        StatusText.Text = $"{files.Count - failed} Dateien nach {outDir} konvertiert ({warnings} Hinweise, {failed} Fehler)";
        if (MessageBox.Show(this, $"{files.Count - failed} Dateien konvertiert.\nZielordner öffnen?", "Ordner konvertieren",
                MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            Process.Start(new ProcessStartInfo("explorer.exe", outDir) { UseShellExecute = true });
    }

    // ------------------------------------------------------------------ Drag & Drop

    void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            LoadFile(files[0]);
    }

    const string SampleSql = """
        -- Beispiel: einfach überschreiben oder eine .sql-Datei hierher ziehen
        CREATE PROCEDURE dbo.usp_KundeSuchen
            @Name NVARCHAR(100),
            @Anzahl INT OUTPUT
        AS
        BEGIN
            SET NOCOUNT ON;
            SELECT @Anzahl = COUNT(*) FROM dbo.Kunde WHERE Name LIKE @Name + '%';

            IF @Anzahl = 0
                RAISERROR('Kein Kunde zu %s gefunden', 16, 1, @Name);

            SELECT TOP 10 KundeId, Name, ISNULL(Email, '-') AS Email,
                   DATEDIFF(day, ErstelltAm, GETDATE()) AS Tage
            FROM dbo.Kunde WITH (NOLOCK)
            WHERE Name LIKE @Name + '%'
            ORDER BY Name;
        END
        """;
}
