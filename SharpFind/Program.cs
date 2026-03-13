using System.Data.OleDb;
using System.Reflection;
using System.Runtime.Versioning;

// --- 変数の初期化 ---
var searchPatterns = new List<string>();
var excludePatterns = new List<string>();
string? targetExt = null;
bool ignoreCase = false;
bool excludeIgnoreCase = false;
const int defaultHeadLimit = 10;
int? headLimit = defaultHeadLimit;
long? largerThan = null;
long? smallerThan = null;
DateTime? newerThan = null;
DateTime? olderThan = null;
bool detailedList = false;
bool noIgnoreHidden = false;
bool useIndexSearch = false;
string scope = "";
string currentDir = Directory.GetCurrentDirectory();

// --- 引数の解析 ---
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--path":
            if (i + 1 < args.Length) searchPatterns.Add(args[++i]);
            break;
        case "--ipath":
            if (i + 1 < args.Length) { searchPatterns.Add(args[++i]); ignoreCase = true; }
            break;
        case "--exclude":
        case "-x":
            if (i + 1 < args.Length) excludePatterns.Add(args[++i]);
            break;
        case "--iexclude":
            if (i + 1 < args.Length) { excludePatterns.Add(args[++i]); excludeIgnoreCase = true; }
            break;
        case "--ext":
            if (i + 1 < args.Length)
            {
                targetExt = args[++i];
                if (!targetExt.StartsWith(".")) targetExt = "." + targetExt;
            }
            break;
        case "--larger":
            if (i + 1 < args.Length) largerThan = ParseSize(args[++i]);
            break;
        case "--smaller":
            if (i + 1 < args.Length) smallerThan = ParseSize(args[++i]);
            break;
        case "--newer":
            if (i + 1 < args.Length) newerThan = ParseDateTime(args[++i]);
            break;
        case "--older":
            if (i + 1 < args.Length) olderThan = ParseDateTime(args[++i]);
            break;
        case "--list":
        case "-l":
            detailedList = true;
            break;
        case "--head":
            // 次の引数が数値ならそれを採用、そうでなければデフォルト値
            if (i + 1 < args.Length && int.TryParse(args[i + 1], out int limit))
            {
                headLimit = limit;
                i++; // 数値として消費したのでインデックスを進める
            }
            else
            {
                headLimit = defaultHeadLimit;
            }
            break;
        case "--heads":
            headLimit = defaultHeadLimit;
            break;
        case "--no-ignore-hidden":
            noIgnoreHidden = true;
            break;
        case "--use-index":
            useIndexSearch = true;
            break;
        case "--scope":
            if (i + 1 < args.Length) scope = args[++i].ToLower();
            break;
        case "--help":
            ShowHelp();
            return;
        case "--version":
        case "-V":
            ShowVersion();
            return;
        default:
            if (!args[i].StartsWith("-")) searchPatterns.Add(args[i]);
            else ShowWarning($"未知のオプション '{args[i]}' は無視されます");
            break;
    }
}

// サイズor更新時刻フィルタの有無
bool hasSizeOrDateFilter = largerThan.HasValue || smallerThan.HasValue || newerThan.HasValue || olderThan.HasValue;

// 実行判定：パターンが空でも、サイズ制限などがあれば実行する
bool hasAnyFilter = searchPatterns.Any() || hasSizeOrDateFilter || !string.IsNullOrEmpty(targetExt);
if (!hasAnyFilter)
{
    Console.WriteLine("SharpFind 使い方: sf [検索文字列] [オプション]");
    return;
}

// --- 実行処理 ---
var options = new EnumerationOptions
{
    IgnoreInaccessible = true,
    RecurseSubdirectories = true
};

// デフォルト除外パターン
var defaultExcludeDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    ".git", "bin", "obj"
};
if (noIgnoreHidden)
{
    defaultExcludeDirs.Clear();
}

// SCOPEに許可された値以外が指定された場合は警告して "sub" を採用、それ以外は指定がない場合も "sub" を採用
if (!string.IsNullOrWhiteSpace(scope) && scope != "all" && scope != "drive" && scope != "sub")
{
    ShowWarning($"無効な SCOPE '{scope}', 'sub' で続行します");
}
if (string.IsNullOrWhiteSpace(scope) || scope != "all" && scope != "drive" && scope != "sub")
{
    scope = "sub";
}

// インデックスサーチ
if (useIndexSearch)
{
    // インデックス検索の実行（サイズとパスキーワードはクエリ内で解決済み）
    var results = SearchViaIndex(currentDir, searchPatterns, largerThan, smallerThan, newerThan, olderThan, targetExt, scope);

    int matchCount = 0;

    foreach (var fileData in results)
    {
        // ローカル用のフィルタを再度適用（除外パターンなど、クエリで解決できないもの）
        // 既に FileData を持っている場合はそれを使う（サイズ・更新日時などを再利用）
        bool isMatch = ApplyFilters(
            fileData,
            null,
            currentDir,
            targetExt,
            defaultExcludeDirs,
            noIgnoreHidden,
            excludePatterns,
            excludeIgnoreCase,
            searchPatterns,
            ignoreCase,
            hasSizeOrDateFilter,
            largerThan,
            smallerThan,
            newerThan,
            olderThan).HasValue;

        if (!isMatch) continue;

        // すべてをパスしたものを表示
        DisplayFile(fileData, currentDir, detailedList);

        // head 判定
        matchCount++;
        if (headLimit.HasValue && matchCount >= headLimit.Value) break;
    }
    return;
}

// 全ドライブまたはドライブ単位での検索
if (scope == "all" || scope == "drive")
{
    // 全ドライブを対象にするため、論理ドライブをすべて取得してループ回す
    // ※通常検索の "all" は非常に時間がかかるため、リミットが無い場合は注意が必要
    var roots = GetSearchRoots(currentDir, scope);

    // 全てのドライブ（ルート）の列挙を一本のストリームに繋げる
    var allFiles = roots.SelectMany(root =>
        Directory.EnumerateFiles(root, "*", options)
    );

    // フィルタとHead（打ち切り）を適用
    var results = allFiles
        .Select(file => ApplyFilters(
            null,
            file,
            currentDir,
            targetExt,
            defaultExcludeDirs,
            noIgnoreHidden,
            excludePatterns,
            excludeIgnoreCase,
            searchPatterns,
            ignoreCase,
            hasSizeOrDateFilter,
            largerThan,
            smallerThan,
            newerThan,
            olderThan))
        .Where(fd => fd.HasValue)
        .Select(fd => fd.Value)
        .Take(headLimit ?? int.MaxValue);

    foreach (var fileData in results)
    {
        DisplayFile(fileData, currentDir, detailedList);
    }
    return;
}

// カレントディレクトリを検索
try
{
    var files = Directory.EnumerateFiles(currentDir, "*", options);

    int matchCount = 0;

    foreach (var file in files)
    {
        var fileData = ApplyFilters(
            null,
            file,
            currentDir,
            targetExt,
            defaultExcludeDirs,
            noIgnoreHidden,
            excludePatterns,
            excludeIgnoreCase,
            searchPatterns,
            ignoreCase,
            hasSizeOrDateFilter,
            largerThan,
            smallerThan,
            newerThan,
            olderThan);

        if (!fileData.HasValue) continue;

        DisplayFile(fileData.Value, currentDir, detailedList);

        // カウントアップと打ち切り判定
        matchCount++;
        if (headLimit.HasValue && matchCount >= headLimit.Value)
        {
            break; // ループを抜けて探索を終了
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"予期せぬエラー: {ex.Message}");
}

// --- 検索ルートの取得 --- 
IEnumerable<string> GetSearchRoots(string currentRoot, string scope)
{
    return scope switch
    {
        "all" => DriveInfo.GetDrives()
                    .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                    .Select(d => d.Name),
        "drive" => new[] { Path.GetPathRoot(Path.GetFullPath(currentRoot)) ?? "C:\\" },
        _ => new[] { currentRoot } // sub
    };
}

// --- サイズ解析用のメソッド (Program.cs の下部に追加) ---
static long? ParseSize(string input)
{
    if (string.IsNullOrWhiteSpace(input)) return null;

    // 大文字小文字を区別しないために小文字化し、前後の空白を除く
    string cleanInput = input.ToLower().Trim();
    long multiplier = 1;

    if (cleanInput.EndsWith("gb"))
    {
        multiplier = 1024L * 1024 * 1024;
        cleanInput = cleanInput.Replace("gb", "");
    }
    else if (cleanInput.EndsWith("mb"))
    {
        multiplier = 1024L * 1024;
        cleanInput = cleanInput.Replace("mb", "");
    }
    else if (cleanInput.EndsWith("kb"))
    {
        multiplier = 1024L;
        cleanInput = cleanInput.Replace("kb", "");
    }

    // 残った数値部分をパース
    if (long.TryParse(cleanInput, out long value))
    {
        return value * multiplier;
    }

    Console.WriteLine($"警告: サイズ '{input}' を正しく解析できませんでした。");
    return null;
}

// --- インデックスサーチ ---
[SupportedOSPlatform("windows")]
static IEnumerable<FileData> SearchViaIndex(string rootPath, List<string> includePatterns, long? largerThan, long? smallerThan, DateTime? newerThan, DateTime? olderThan, string? targetExt, string scope)
{
    string connectionString = "Provider=Search.CollatorDSO;Extended Properties='Application=Windows';";

    using var connection = new OleDbConnection(connectionString);
    connection.Open();

    var conditions = new List<string>();

    string fullPathSlash = Path.GetFullPath(rootPath).Replace("\\", "/");
    string driveRoot = Path.GetPathRoot(fullPathSlash)?.Replace("\\", "/")?.TrimEnd('/') ?? "C:";

    // SCOPEの指定に応じてクエリ条件を追加
    switch (scope)
    {
        case "all":
            // SCOPEを指定しない（インデックス全域が対象）
            break;
        case "drive":
            conditions.Add($"SCOPE='file:{driveRoot}'");
            break;
        case "sub":
        default:
            conditions.Add($"SCOPE='file:{fullPathSlash}'");
            break;
    }

    // サイズ指定があればクエリに含める（DB側でフィルタリング）
    if (largerThan.HasValue) conditions.Add($"System.Size >= {largerThan.Value}");
    if (smallerThan.HasValue) conditions.Add($"System.Size <= {smallerThan.Value}");
    // 更新日時指定があればクエリに含める（DB側でフィルタリング）
    if (newerThan.HasValue) conditions.Add($"System.DateModified >= '{newerThan.Value:yyyy-MM-dd HH:mm:ss}'");
    if (olderThan.HasValue) conditions.Add($"System.DateModified <= '{olderThan.Value:yyyy-MM-dd HH:mm:ss}'");

    // キーワード指定（AND条件）
    foreach (var p in includePatterns)
    {
        // 文字列のサニタイズ（シングルクォート対策）
        string escapedP = p.Replace("'", "''");
        conditions.Add($"System.ItemPathDisplay LIKE '%{escapedP}%'");
    }

    // SearchViaIndex メソッド内
    if (!string.IsNullOrEmpty(targetExt))
    {
        // Windows SearchのSystem.FileExtensionはドットを含む (例: ".cs")
        conditions.Add($"System.FileExtension = '{targetExt.ToLower()}'");
    }

    string whereClause = string.Join(" AND ", conditions);
    // フォルダではなく「ファイル」のみに絞る条件も追加可能
    string query = $"SELECT System.ItemPathDisplay, System.Size, System.DateModified FROM SystemIndex WHERE {whereClause}"; //  AND System.ContentType <> 'directory'

    using var command = new OleDbCommand(query, connection);
    using var reader = command.ExecuteReader();
    while (reader.Read())
    {
        string fullPath = reader.GetValue(0)?.ToString() ?? string.Empty;
        // OLE DB の数値型（Decimal等）を long に安全に変換
        long size = reader.IsDBNull(1) ? 0L : Convert.ToInt64(reader.GetValue(1));
        DateTime modified = reader.IsDBNull(2) ? DateTime.MinValue : (DateTime)reader.GetValue(2);

        yield return new FileData(
            fullPath,
            Path.GetRelativePath(rootPath, fullPath),
            size,
            modified
        );
    }
}

// --- ファイルフィルタの適用 ---
static FileData? ApplyFilters(
    FileData? existing,
    string? filePath,
    string currentDir,
    string? targetExt,
    HashSet<string> defaultExcludeDirs,
    bool noIgnoreHidden,
    List<string> excludePatterns,
    bool excludeIgnoreCase,
    List<string> searchPatterns,
    bool ignoreCase,
    bool hasSizeOrDateFilter,
    long? largerThan,
    long? smallerThan,
    DateTime? newerThan,
    DateTime? olderThan)
{
    // Use provided FileData if available; otherwise construct from filePath
    FileData data;
    if (existing != null)
    {
        data = existing.Value;
    }
    else
    {
        if (string.IsNullOrEmpty(filePath)) return null; // cannot construct

        // 拡張子フィルタ will be checked below using filePath
        var fi = new FileInfo(filePath);
        data = new FileData(
            filePath,
            Path.GetRelativePath(currentDir, filePath),
            fi.Length,
            fi.LastWriteTime);
    }

    // 拡張子フィルタ
    if (!string.IsNullOrEmpty(targetExt))
    {
        if (!string.Equals(Path.GetExtension(data.FullPath), targetExt, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
    }

    string relativePath = data.RelativePath;

    // デフォルト除外の判定
    if (!noIgnoreHidden)
    {
        var pathSegments = relativePath.Split(Path.DirectorySeparatorChar);
        bool isDefaultExcluded = pathSegments.SkipLast(1).Any(segment => defaultExcludeDirs.Contains(segment));
        if (isDefaultExcluded) return null;
    }

    // 除外フィルタリング
    var xComp = excludeIgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    if (excludePatterns.Any(p => relativePath.Contains(p, xComp))) return null;

    // 検索フィルタリング（AND条件）
    var sComp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    if (searchPatterns.Count > 0 && !searchPatterns.All(p => relativePath.Contains(p, sComp))) return null;

    if (hasSizeOrDateFilter)
    {
        if (largerThan.HasValue && data.Size < largerThan.Value) return null;
        if (smallerThan.HasValue && data.Size > smallerThan.Value) return null;

        var lastWriteTime = data.Modified;
        if (newerThan.HasValue && lastWriteTime < newerThan.Value) return null;
        if (olderThan.HasValue && lastWriteTime > olderThan.Value) return null;
    }

    return data;
}

// --- ファイル表示 ---
static void DisplayFile(FileData data, string currentDir, bool detailedList)
{
    // --- 詳細情報の表示 (日付・サイズ) ---
    if (detailedList)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray; // 控えめな色
        Console.Write($"{data.Modified:yyyy-MM-dd HH:mm}  ");

        Console.ForegroundColor = ConsoleColor.Cyan; // 数値は少し目立たせる
        Console.Write($"{FormatSize(data.Size).PadLeft(8)}  ");
    }

    // --- パスの色分け表示 ---
    string path = ResolveDisplayPath(data.FullPath, data.RelativePath, currentDir);
    int lastSeparator = path.LastIndexOf(Path.DirectorySeparatorChar);

    if (lastSeparator >= 0)
    {
        // ディレクトリ部分は暗めのグレー
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write(path.Substring(0, lastSeparator + 1));

        // ファイル名は明るい白
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine(path.Substring(lastSeparator + 1));
    }
    else
    {
        // ルート直下の場合はそのまま白で表示
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine(path);
    }

    Console.ResetColor(); // 最後に必ず色を戻す
}

// --- 表示用のパス解決 ---
static string ResolveDisplayPath(string fullPath, string relPath, string currentDir)
{
    // 相対パスが長すぎる(3階層以上の)場合は、絶対パスを表示する
    string[] parts = relPath.Split(Path.DirectorySeparatorChar);
    int parentDirCount = 0;
    foreach (var part in parts)
    {
        if (part == "..") parentDirCount++;
        else break; // 先頭から連続する ".." だけをカウント
    }
    if (parentDirCount >= 3)
    {
        return fullPath;
    }

    return relPath;
}

// --- サイズ表記 ---
static string FormatSize(long bytes)
{
    if (bytes >= 1024L * 1024 * 1024) // GB
        return $"{(double)bytes / (1024 * 1024 * 1024):F1}GB";
    if (bytes >= 1024 * 1024) // MB
        return $"{(double)bytes / (1024 * 1024):F1}MB";
    if (bytes >= 1024) // KB
        return $"{(double)bytes / 1024:F1}KB";

    return $"{bytes}byte"; // 1KB未満はバイト表示
}

// --- 日時文字列を日時に変換 ---
static DateTime? ParseDateTime(string input)
{
    if (string.IsNullOrWhiteSpace(input)) return null;

    // 相対日時の解析 (1d, 1h, 1m, 1s)
    var match = System.Text.RegularExpressions.Regex.Match(input, @"^(\d+)([dhms])$");
    if (match.Success)
    {
        int value = int.Parse(match.Groups[1].Value);
        return match.Groups[2].Value switch
        {
            "d" => DateTime.Now.AddDays(-value),
            "h" => DateTime.Now.AddHours(-value),
            "m" => DateTime.Now.AddMinutes(-value),
            "s" => DateTime.Now.AddSeconds(-value),
            _ => null
        };
    }

    // 絶対日時の解析
    string[] formats = {
        "yyyy", "yyyyMMdd", "yyyyMMdd HHmm",
        "yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss", "yyyy/MM/dd", "yyyy/MM/dd HH:mm:ss"
    };

    if (DateTime.TryParseExact(input, formats, null, System.Globalization.DateTimeStyles.None, out var dt))
    {
        return dt;
    }

    return null;
}

// --- 警告メッセージの表示 ---
static void ShowWarning(string message)
{
    try
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Error.WriteLine($"警告: {message}");
    }
    finally
    {
        Console.Out.Flush();
        Console.Error.Flush();
        Console.ResetColor();
    }
}

// --- ヘルプ表示 ---
static void ShowHelp()
{
    const string helpText = @"
SharpFind (sf) - Windows Indexer-based Fast File Search Tool

Usage:
  sf [keyword...] [options]

  指定された条件を満たすファイルを表示します
  インデックス検索でない場合は、カレントディレクトリをルートとして検索します 
  デフォルトでは、--head 10, --scope sub が指定された状態になります

Arguments:
  keyword                 検索キーワード --pathと同じ (複数指定時はAND条件)

Options:
  -l, --list              詳細表示モード。サイズと更新日時を合わせて表示します
  --use-index             Windows Search インデックスを使用して高速検索を行います
  --scope <path>          検索の範囲を指定
                          (sub: 指定パス以下, drive: ドライブ全体, all: 全ドライブ)
  --head <num>            検索結果を指定した件数で打ち切ります
  --no-ignore-hidden      デフォルトで除外される .git, bin, obj 等を含めて表示
  --help                  このヘルプを表示します
  -V, --version           バージョンを表示します

Filtering:
  --path <keyword>        パス文字列に含まれる検索キーワードを指定 (複数指定時はAND条件)
  -x, --exclude <keyword> 除外キーワード (複数指定時はOR条件)
  --ext <extension>       拡張子を指定
  --larger <size>         指定サイズ以上 (例: 100mb, 1.5gb, 500kb)
  --smaller <size>        指定サイズ以下
  --newer <date/datetime> 指定日時以降 (例: 1d, 2h, 2026, '20260101 1230')
  --older <date/datetime> 指定日時以前

Examples:
  sf --use-index .jpg --older 20250930 --newer 20231001
  sf log --larger 10mb -l
";
    Console.WriteLine(helpText);
}

// --- バージョン表示用のメソッド ---
static void ShowVersion()
{
    var assembly = Assembly.GetExecutingAssembly();
    var version = assembly.GetName().Version;

    // バージョン
    var infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    // コミットハッシュ
    string commitHash = "unknown";
    if (infoVersion != null && infoVersion.Contains("+"))
    {
        commitHash = infoVersion.Split('+')[1];
        if (commitHash.Length > 7) commitHash = commitHash.Substring(0, 7);
    }

    // ビルド日時
    string buildDateStr;
    try
    {
        // 実行ファイル自体の最終更新日時を出す
        string path = AppContext.BaseDirectory;
        buildDateStr = File.GetLastWriteTime(Path.Combine(path, AppDomain.CurrentDomain.FriendlyName + ".exe")).ToString("yyyy-MM-dd HH:mm:ss");
    }
    catch
    {
        buildDateStr = "2026-01-01"; // 取得できない場合のデフォルト
    }

    Console.WriteLine($"SharpFind version {version?.Major}.{version?.Minor}.{version?.Build} " +
                      $"(build: {buildDateStr}, commit: {commitHash})");
}
readonly struct FileData
{
    public string FullPath { get; init; }
    public string RelativePath { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }

    public FileData(string fullPath, string relativePath, long size, DateTime modified)
    {
        FullPath = fullPath;
        RelativePath = relativePath;
        Size = size;
        Modified = modified;
    }
}
