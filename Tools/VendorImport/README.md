# Vendor Import Utility

One-time importer for Neighborhood Vendor data from Excel into normalized JSON payloads.

## Run

```powershell
dotnet run --project Tools/VendorImport/VendorImport.csproj -- "C:\Users\<you>\Downloads\Canyon Oaks Vendors.xlsx" "C:\temp\vendor-import-output"
```

Second argument is optional. If omitted, output is written to `vendor-import-output` under the current directory.

## Output files

- `vendors.json` - normalized vendor records
- `vendor-reviews.json` - text-only vendor reviews (`Referrer Name` + `Review`)
- `youth-services.json` - normalized babysitter/tutor-style records
- `row-outcomes.json` - per-row import decision log
- `report.json` - import totals (imported/skipped)

## Notes

- Dedupes vendors by deterministic fingerprint (`normalizedName + phone + email`, SHA-256).
- Classifies youth-service rows using service/category keywords plus youth-specific fields (born year / parent note).
- Designed as a repeatable one-time migration helper; resulting JSON can be posted through API endpoints.
