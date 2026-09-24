using GManager.Core;
using Xunit;

namespace GManager.Tests;

public class CredentialStoreTests
{
    private const string TestResource = "GManager_UnitTest";

    [Fact]
    public void CredentialStore_SetGetRemove_WorksWithWindowsPasswordVault()
    {
        var store = new CredentialStore(TestResource);
        var testKey = $"test_user_{Guid.NewGuid():N}";
        var testSecret = "1//04test_refresh_token_xyz987";

        try
        {
            // Initial state: not found
            Assert.Null(store.GetCredential(testKey));

            // Set credential
            store.SetCredential(testKey, testSecret);

            // Get credential
            var retrieved = store.GetCredential(testKey);
            Assert.Equal(testSecret, retrieved);

            // Update existing credential (should not throw duplicate error)
            var updatedSecret = "1//04updated_secret_abc123";
            store.SetCredential(testKey, updatedSecret);
            Assert.Equal(updatedSecret, store.GetCredential(testKey));

            // Remove credential
            var removed = store.RemoveCredential(testKey);
            Assert.True(removed);
            Assert.Null(store.GetCredential(testKey));
        }
        finally
        {
            store.RemoveCredential(testKey);
        }
    }
}
