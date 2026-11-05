using System.Security.Claims;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ShortenerTools.Admin;

namespace ShortenerTools.Tests;

[TestClass]
public class AdministratorAccessTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Administrator = "22222222-2222-2222-2222-222222222222";
    private const string Other = "33333333-3333-3333-3333-333333333333";

    [TestMethod]
    public void ConfiguredAdministratorIsAllowed() =>
        Assert.IsTrue(AdministratorAccess.IsAllowed(Identity(Tenant, Administrator), Tenant, Administrator));

    [TestMethod]
    [DataRow(Other, Administrator)]
    [DataRow(Tenant, Other)]
    [DataRow("", Administrator)]
    [DataRow(Tenant, "")]
    public void OtherOrIncompleteIdentityIsRejected(string tenant, string objectId) =>
        Assert.IsFalse(AdministratorAccess.IsAllowed(Identity(tenant, objectId), Tenant, Administrator));

    [TestMethod]
    public void AnonymousIdentityIsRejected() =>
        Assert.IsFalse(AdministratorAccess.IsAllowed(new ClaimsPrincipal(), Tenant, Administrator));

    private static ClaimsPrincipal Identity(string tenant, string objectId) =>
        new(new ClaimsIdentity([new("tid", tenant), new("oid", objectId)], "oidc"));
}
