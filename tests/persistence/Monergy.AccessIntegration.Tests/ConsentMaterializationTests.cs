using System.Data;
using System.Globalization;
using Dapper;
using Monergy.Services.Consent;
using Xunit;

namespace Monergy.AccessIntegration.Tests;

public sealed class ConsentMaterializationTests
{
    [Fact]
    public void EmptyConsentListAcceptsProviderArrayMetadata()
    {
        using var table = ProviderColumns();
        using var reader = table.CreateDataReader();
        Assert.Equal(typeof(Array), reader.GetFieldType(4));
        // QueryAsync constructs this parser even when no grants exist yet.
        Assert.NotNull(reader.GetRowParser<PostgresCustomerConsent.GrantRow>());
        Assert.False(reader.Read());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GrantMaterializationPreservesCapabilitiesExpiryAndRevocation(bool revoked)
    {
        using var table = ProviderColumns();
        string[] capabilities = ["financial-profile.profile.read", "search.query.execute"];
        var expires = new DateTime(2026, 10, 11, 10, 0, 0, DateTimeKind.Utc);
        var revokedAt = expires.AddHours(-1);
        table.Rows.Add("grant-1", "customer-1", "advisor-1", "customer-advice", capabilities, expires,
            revoked ? revokedAt : DBNull.Value);
        using var reader = table.CreateDataReader();
        var parse = reader.GetRowParser<PostgresCustomerConsent.GrantRow>();
        Assert.True(reader.Read());
        var grant = parse(reader);
        Assert.Equal("grant-1", grant.Id);
        Assert.Equal("customer-1", grant.CustomerId);
        Assert.Equal("advisor-1", grant.ActorId);
        Assert.Equal("customer-advice", grant.Purpose);
        Assert.Equal(capabilities, grant.CapabilityIds);
        Assert.Equal(expires, grant.ExpiresAt);
        Assert.Equal(DateTimeKind.Utc, grant.ExpiresAt.Kind);
        Assert.Equal(revoked ? revokedAt : (DateTime?)null, grant.RevokedAt);
        Assert.False(reader.Read());
    }

    private static DataTable ProviderColumns()
    {
        var table = new DataTable { Locale = CultureInfo.InvariantCulture };
        foreach (var name in new[] { "id", "customerid", "actorid", "purpose" }) table.Columns.Add(name, typeof(string));
        // Npgsql 10 reports text[] as System.Array, while values are string[].
        // Use the exact metadata and lowercase aliases observed in Docker UAT #23.
        table.Columns.Add("capabilityids", typeof(Array));
        table.Columns.Add("expiresat", typeof(DateTime)).DateTimeMode = DataSetDateTime.Utc;
        table.Columns.Add("revokedat", typeof(DateTime)).DateTimeMode = DataSetDateTime.Utc;
        return table;
    }
}
