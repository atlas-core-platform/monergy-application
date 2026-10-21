# UAT disabled-product route initialization repair

The default retained profile disables Product services. Document Vault mapped handlers unconditionally, so ASP.NET inferred the unregistered CustomerOwnerClient as a request body on GET and failed endpoint initialization for every request, including health and the access shell.

MapProductDocumentVault now observes the same Monergy:Product:Enabled guard as AddProductWorkspace and the other product endpoint groups. Enabled product routes and authorization behavior are unchanged; disabled routes remain absent.

Verification: reproduced the exact inferred-body HTTP 500 with the actual pre-fix gateway. After the fix, the gateway builds with zero warnings/errors and verify-gateway-startup.py passes over actual loopback HTTP with Product disabled and enabled. Both serve the fixture access shell and return liveness 200; readiness correctly returns 503 with owner services deliberately absent. Disabled vault requests return 404. The test creates only temporary content and does not access retained profiles. Full Docker stack readiness is not claimed.
