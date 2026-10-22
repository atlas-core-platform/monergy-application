# Supported document intake

Customers can upload PDF,PNG,JPEG,UTF-8 text/CSV and XLSX originals up to1 MiB.
Current customer/document permissions,content-type validation and a successful
malware scan are required before retention. Original bytes,hash,version and
provider metadata remain distinct from classification and financial results.

XLSX intake accepts valid ordinary and multi-sheet workbooks independently of the
financial template. It checks workbook/worksheet structure and package bounds:
512 entries,50 sheets,20 MiB expanded total,10 MiB per entry and1 MiB per interpreted
XML part. Macros,duplicate/traversal paths,missing worksheets and XML entities are
rejected. No formula or external resource is executed or fetched. Legacy XLS is
not supported. Financial extraction still requires the documented supported
template; a retained workbook is not automatically an extracted financial record.

For retained tenant storage, supply the existing Evidence storage configuration
during onboarding. For retained classification/OCR, supply the Classification
configuration and apply its owner migrations. Unconfigured reference stores are
volatile. Live storage/scanner/database and browser UAT remain operator validation;
production retention/key custody is not approved by local engineering completion.
