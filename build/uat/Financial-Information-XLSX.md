# Financial information XLSX template

In Documents, download **Excel template** and replace its synthetic example row
with your own information. Save as `.xlsx` and upload using **Upload document**.
The template supports one visible sheet and1–100 data rows with exactly these
columns (each once): Category, Description, Amount, Currency, Date, Source.

- Category: INCOME, EXPENSE, LOAN, ASSET, INVESTMENT, INSURANCE or TAX.
- Description and Source: plain nonempty text describing the figure and its source.
- Amount: nonnegative plain decimal within the existing decimal/length bounds. Use General numeric
  formatting or text; currency symbols, grouping separators and exponent notation
  are unsupported. This is a source figure, not a complete account record.
- Currency: three ASCII letters, normalized to uppercase.
- Date: exact YYYY-MM-DD **text**, as demonstrated in the template. Excel date
  serials/automatic date formatting are unsupported.

Use plain cells without formulas (even with cached results), links, macros,
hidden rows/columns/sheets, merges, custom number formats or rich text. Legacy
`.xls`, macro-enabled `.xlsm`, arbitrary bank layouts and multiple sheets are
unsupported. The upload limit is1MiB and conservative expanded-archive limits
also apply. Ordinary theme/property metadata saved by common writers is allowed;
not every writer or workbook layout is supported.

The configured malware gate must accept the original bytes before intake.
Choose Process, review the copied Excel figures and explicitly confirm only the
facts you want to adopt. Upload/extraction alone never updates financial truth.
An unsupported/damaged workbook yields no candidate facts; correct the workbook
and upload a new version rather than treating failed extraction as a zero balance.

Local native tests use explicit scanner/repository doubles. Live Docker/ClamAV,
durable storage and integrated UAT remain separate unexecuted checks. The exact
implementation receipt is build/governance/t1-57-financial-xlsx-intake.md.
