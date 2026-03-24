using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Data;
using System.Text.Json;
using ExcelDataReader;

if (args.Length < 1)
{
    Console.WriteLine("Usage: dotnet run --project Tools/VendorImport/VendorImport.csproj -- <xlsxPath> [outputDir]");
    return;
}

var inputPath = args[0];
var outputDir = args.Length > 1 ? args[1] : Path.Combine(Directory.GetCurrentDirectory(), "vendor-import-output");
Directory.CreateDirectory(outputDir);

if (!File.Exists(inputPath))
{
    Console.Error.WriteLine($"Input file not found: {inputPath}");
    return;
}

var vendors = new List<VendorImportRecord>();
var vendorReviews = new List<VendorReviewImportRecord>();
var youthServices = new List<YouthServiceImportRecord>();
var rowOutcomes = new List<RowOutcome>();
var seenFingerprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

{
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    using var stream = File.Open(inputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var reader = ExcelReaderFactory.CreateReader(stream);
    var dataSet = reader.AsDataSet(new ExcelDataSetConfiguration
    {
        ConfigureDataTable = _ => new ExcelDataTableConfiguration { UseHeaderRow = false }
    });

    foreach (DataTable worksheet in dataSet.Tables)
    {
        if (worksheet.Rows.Count == 0 || worksheet.Columns.Count == 0)
        {
            continue;
        }

        var headerRowIndex = DetectHeaderRowIndex(worksheet);
        var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var c = 0; c < worksheet.Columns.Count; c++)
        {
            var header = NormalizeHeader(worksheet.Rows[headerRowIndex][c]?.ToString() ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(header) && !headers.ContainsKey(header))
            {
                headers[header] = c;
            }
        }

        for (var rowIndex = headerRowIndex + 1; rowIndex < worksheet.Rows.Count; rowIndex++)
        {
            var row = worksheet.Rows[rowIndex];
            var raw = ReadRawRow(row, headers, worksheet.TableName);
            if (IsEmpty(raw))
            {
                continue;
            }

            var normalized = Normalize(raw);
            if (!normalized.IsValid)
            {
                rowOutcomes.Add(new RowOutcome(worksheet.TableName, rowIndex + 1, "skipped", normalized.Reason ?? "Invalid row"));
                continue;
            }

            if (!seenFingerprints.Add(normalized.Fingerprint))
            {
                rowOutcomes.Add(new RowOutcome(worksheet.TableName, rowIndex + 1, "skipped", "Duplicate fingerprint"));
                continue;
            }

            if (normalized.IsYouthService)
            {
                youthServices.Add(new YouthServiceImportRecord(
                    normalized.Fingerprint,
                    normalized.Name,
                    normalized.Services,
                    normalized.BornYear,
                    normalized.Phone,
                    normalized.ContactMethod,
                    normalized.Email,
                    normalized.Address,
                    normalized.ParentNote));
                rowOutcomes.Add(new RowOutcome(worksheet.TableName, rowIndex + 1, "imported", "Youth service"));
            }
            else
            {
                var vendorRecord = new VendorImportRecord(
                    normalized.Fingerprint,
                    normalized.Name,
                    normalized.Categories,
                    normalized.IsNeighborAffiliated,
                    normalized.Phone,
                    normalized.Email,
                    normalized.Website,
                    normalized.Address,
                    normalized.Notes);
                vendors.Add(vendorRecord);
                if (!string.IsNullOrWhiteSpace(normalized.ReviewText))
                {
                    vendorReviews.Add(new VendorReviewImportRecord(
                        ComputeReviewFingerprint(vendorRecord.Fingerprint, normalized.ReferrerName, normalized.ReviewText),
                        vendorRecord.Fingerprint,
                        string.IsNullOrWhiteSpace(normalized.ReferrerName) ? "Community Referrer" : normalized.ReferrerName,
                        normalized.ReviewText));
                }
                rowOutcomes.Add(new RowOutcome(worksheet.TableName, rowIndex + 1, "imported", "Vendor"));
            }
        }
    }
}

var options = new JsonSerializerOptions { WriteIndented = true };
File.WriteAllText(Path.Combine(outputDir, "vendors.json"), JsonSerializer.Serialize(vendors, options));
File.WriteAllText(Path.Combine(outputDir, "vendor-reviews.json"), JsonSerializer.Serialize(vendorReviews, options));
File.WriteAllText(Path.Combine(outputDir, "youth-services.json"), JsonSerializer.Serialize(youthServices, options));
File.WriteAllText(Path.Combine(outputDir, "row-outcomes.json"), JsonSerializer.Serialize(rowOutcomes, options));

var report = new
{
    source = inputPath,
    importedVendors = vendors.Count,
    importedVendorReviews = vendorReviews.Count,
    importedYouthServices = youthServices.Count,
    skippedRows = rowOutcomes.Count(r => r.Outcome == "skipped"),
    totalRowsProcessed = rowOutcomes.Count
};
File.WriteAllText(Path.Combine(outputDir, "report.json"), JsonSerializer.Serialize(report, options));

Console.WriteLine($"Imported vendors: {vendors.Count}");
Console.WriteLine($"Imported vendor reviews: {vendorReviews.Count}");
Console.WriteLine($"Imported youth services: {youthServices.Count}");
Console.WriteLine($"Skipped rows: {rowOutcomes.Count(r => r.Outcome == "skipped")}");
Console.WriteLine($"Output directory: {outputDir}");

static Dictionary<string, string> ReadRawRow(DataRow row, IReadOnlyDictionary<string, int> headers, string sheetName)
{
    string Read(params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (headers.TryGetValue(candidate, out var index))
            {
                return (row[index]?.ToString() ?? string.Empty).Trim();
            }
        }

        return string.Empty;
    }

    var name = Read("name", "vendor", "vendor name", "business", "business name", "company", "provider", "provider name", "contact");
    if (string.IsNullOrWhiteSpace(name))
    {
        for (var i = 0; i < row.Table.Columns.Count; i++)
        {
            var candidate = (row[i]?.ToString() ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                name = candidate;
                break;
            }
        }
    }

    return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["name"] = name,
        ["category"] = Read("category", "categories", "type"),
        ["services"] = Read("services", "service", "offered services"),
        ["neighbor"] = Read("neighbor owned", "neighbor affiliated", "neighbor", "hoa resident"),
        ["phone"] = Read("phone", "phone number", "cell"),
        ["contactMethod"] = Read("text/call", "contact method", "text or call"),
        ["email"] = Read("email", "email address"),
        ["website"] = Read("website", "url"),
        ["address"] = Read("address", "street address"),
        ["notes"] = Read("notes"),
        ["reviewText"] = Read("review", "testimonial"),
        ["referrerName"] = Read("referrer name", "referrer", "referred by"),
        ["parentNote"] = Read("parent note", "parent notes"),
        ["bornYear"] = Read("born", "born year", "birth year"),
        ["sheetCategory"] = sheetName
    };
}

static bool IsEmpty(Dictionary<string, string> raw) =>
    raw.Values.All(v => string.IsNullOrWhiteSpace(v));

static NormalizedRow Normalize(Dictionary<string, string> raw)
{
    var name = (raw["name"] ?? string.Empty).Trim();
    var categories = NormalizeList(raw["category"]);
    if (categories.Count == 0 && !string.IsNullOrWhiteSpace(raw["sheetCategory"]) && !raw["sheetCategory"].Contains("Babysitters", StringComparison.OrdinalIgnoreCase))
    {
        categories = NormalizeList(raw["sheetCategory"]);
    }
    var services = NormalizeList(raw["services"]);
    var phone = NormalizePhone(raw["phone"]);
    var email = (raw["email"] ?? string.Empty).Trim().ToLowerInvariant();
    var website = (raw["website"] ?? string.Empty).Trim();
    var address = (raw["address"] ?? string.Empty).Trim();
    var notes = (raw["notes"] ?? string.Empty).Trim();
    var reviewText = (raw["reviewText"] ?? string.Empty).Trim();
    var referrerName = (raw["referrerName"] ?? string.Empty).Trim();
    var parentNote = (raw["parentNote"] ?? string.Empty).Trim();
    var bornYear = ParseYear(raw["bornYear"]);
    var contactMethod = NormalizeContactMethod(raw["contactMethod"]);
    var isNeighbor = ParseBool(raw["neighbor"]) || InferNeighborAffiliated(notes, reviewText, referrerName);

    if (string.IsNullOrWhiteSpace(name))
    {
        return NormalizedRow.Invalid("Missing name");
    }

    var hasContact = !string.IsNullOrWhiteSpace(phone) || !string.IsNullOrWhiteSpace(email) || !string.IsNullOrWhiteSpace(website);
    if (!hasContact && categories.Count == 0 && services.Count == 0)
    {
        return NormalizedRow.Invalid("Missing contact/category/service");
    }

    var youthKeywords = new[] { "babysit", "babysitter", "tutor", "pet sit", "house sit" };
    var mergedServices = new HashSet<string>(services, StringComparer.OrdinalIgnoreCase);
    foreach (var category in categories)
    {
        foreach (var keyword in youthKeywords)
        {
            if (category.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                mergedServices.Add(ToTitleCase(keyword));
            }
        }
    }

    var isYouth = bornYear != null || !string.IsNullOrWhiteSpace(parentNote) || mergedServices.Count > 0;
    var fingerprint = ComputeFingerprint(name, phone, email);
    return new NormalizedRow(
        true,
        null,
        isYouth,
        fingerprint,
        name,
        categories,
        mergedServices.OrderBy(v => v).ToList(),
        isNeighbor,
        phone,
        contactMethod,
        email,
        website,
        address,
        notes,
        reviewText,
        referrerName,
        parentNote,
        bornYear);
}

static int? ParseYear(string value)
{
    if (int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var year))
    {
        if (year >= 1900 && year <= DateTime.UtcNow.Year)
        {
            return year;
        }
    }

    return null;
}

static bool ParseBool(string raw) =>
    string.Equals(raw?.Trim(), "yes", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(raw?.Trim(), "y", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(raw?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

static bool InferNeighborAffiliated(string notes, string reviewText, string referrerName)
{
    var haystack = $"{notes} {reviewText} {referrerName}".ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(haystack))
    {
        return false;
    }

    return haystack.Contains("neighbor:") ||
           haystack.Contains("neighbour:") ||
           haystack.Contains("neighbor ") ||
           haystack.Contains("neighbour ") ||
           haystack.Contains("lives in the neighborhood") ||
           haystack.Contains("lives in our neighborhood") ||
           haystack.Contains("resident at") ||
           haystack.Contains("local resident");
}

static string NormalizeContactMethod(string raw)
{
    if (raw.Contains("call", StringComparison.OrdinalIgnoreCase))
    {
        return "Call";
    }

    return "Text";
}

static List<string> NormalizeList(string value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return new List<string>();
    }

    return value
        .Split(new[] { ',', ';', '/', '|' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(v => v.Replace("&", "and", StringComparison.Ordinal))
        .Select(v => v.Trim())
        .Where(v => v.Length > 0)
        .Select(ToTitleCase)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(v => v)
        .ToList();
}

static string ToTitleCase(string value)
{
    var lowered = value.Trim().ToLowerInvariant();
    return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(lowered);
}

static string NormalizePhone(string phone)
{
    var digits = new string((phone ?? string.Empty).Where(char.IsDigit).ToArray());
    if (digits.Length >= 10)
    {
        return digits.Substring(digits.Length - 10);
    }

    return digits;
}

static string NormalizeHeader(string raw)
{
    var lower = (raw ?? string.Empty).Trim().ToLowerInvariant();
    var chars = lower.Where(ch => char.IsLetterOrDigit(ch) || ch == ' ' || ch == '/' || ch == '&').ToArray();
    return new string(chars);
}

static int DetectHeaderRowIndex(DataTable worksheet)
{
    var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "name", "vendor", "business", "provider", "category", "categories", "type",
        "services", "service", "phone", "phone number", "cell", "text/call",
        "contact method", "text or call", "email", "email address", "website", "url",
        "address", "street address", "notes", "review", "description", "referrer name", "referrer", "referred by", "parent note",
        "parent notes", "born", "born year", "birth year", "neighbor owned",
        "neighbor affiliated", "neighbor", "hoa resident"
    };

    var maxRows = Math.Min(worksheet.Rows.Count, 25);
    var bestIndex = 0;
    var bestScore = -1;

    for (var r = 0; r < maxRows; r++)
    {
        var score = 0;
        for (var c = 0; c < worksheet.Columns.Count; c++)
        {
            var normalized = NormalizeHeader(worksheet.Rows[r][c]?.ToString() ?? string.Empty);
            if (known.Contains(normalized))
            {
                score++;
            }
        }

        if (score > bestScore)
        {
            bestScore = score;
            bestIndex = r;
        }
    }

    return bestIndex;
}

static string ComputeFingerprint(string name, string phone, string email)
{
    var normalizedName = new string(name
        .ToUpperInvariant()
        .Where(char.IsLetterOrDigit)
        .ToArray());
    var key = $"{normalizedName}|{phone}|{email.ToLowerInvariant()}";
    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
    return Convert.ToHexString(hash);
}

static string ComputeReviewFingerprint(string vendorFingerprint, string referrerName, string reviewText)
{
    var key = $"{vendorFingerprint}|{referrerName?.Trim()}|{reviewText?.Trim()}";
    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
    return Convert.ToHexString(hash);
}

public sealed record VendorImportRecord(
    string Fingerprint,
    string Name,
    List<string> Categories,
    bool IsNeighborAffiliated,
    string Phone,
    string Email,
    string Website,
    string Address,
    string Notes);

public sealed record VendorReviewImportRecord(
    string Fingerprint,
    string VendorFingerprint,
    string ReferrerName,
    string ReviewText);

public sealed record YouthServiceImportRecord(
    string Fingerprint,
    string Name,
    List<string> Services,
    int? BornYear,
    string Phone,
    string ContactMethod,
    string Email,
    string Address,
    string ParentNote);

public sealed record RowOutcome(string Sheet, int Row, string Outcome, string Reason);

public sealed record NormalizedRow(
    bool IsValid,
    string? Reason,
    bool IsYouthService,
    string Fingerprint,
    string Name,
    List<string> Categories,
    List<string> Services,
    bool IsNeighborAffiliated,
    string Phone,
    string ContactMethod,
    string Email,
    string Website,
    string Address,
    string Notes,
    string ReviewText,
    string ReferrerName,
    string ParentNote,
    int? BornYear)
{
    public static NormalizedRow Invalid(string reason) =>
        new(false, reason, false, string.Empty, string.Empty, new List<string>(), new List<string>(), false, string.Empty, "Text", string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, null);
}
