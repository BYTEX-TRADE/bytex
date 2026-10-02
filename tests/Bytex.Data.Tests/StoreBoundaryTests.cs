namespace Bytex.Data.Tests;

public sealed class StoreBoundaryTests
{
    [Fact]
    public void A_sibling_with_the_same_root_prefix_is_outside_the_store()
    {
        string root = Path.Combine(Path.GetTempPath(), "bytex-boundary-" + Guid.NewGuid().ToString("N"));
        LocalObjectStore store = new(root);
        try
        {
            string sibling = "../" + Path.GetFileName(root) + "-other/segment.parquet";
            Assert.Throws<ArgumentException>(() => store.Exists(sibling));
            Assert.Throws<ArgumentException>(() => store.List("../" + Path.GetFileName(root) + "-other"));
            Assert.Throws<ArgumentException>(() => store.ReadText(sibling));
        }
        finally
        {
            Directory.Delete(root);
        }
    }
}
