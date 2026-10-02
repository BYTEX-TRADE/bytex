using System.Reflection;
using Bytex.Core.Model.Instruments;
using Xunit.Sdk;

namespace Bytex.Data.Tests.Support;

/// <summary>
/// Instruments are classes without value equality, so a round trip is verified property by property.
/// Reflection is used on purpose: a property added to the model later is compared automatically,
/// and a serializer that forgets it fails this check instead of slipping through.
/// </summary>
internal static class InstrumentAssert
{
    public static void Same(Instrument expected, Instrument actual)
    {
        Assert.Equal(expected.GetType(), actual.GetType());

        PropertyInfo[] properties = expected.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.True(properties.Length >= 25, "the reflection walk found suspiciously few properties");

        foreach (PropertyInfo property in properties)
        {
            object? want = property.GetValue(expected);
            object? have = property.GetValue(actual);
            try
            {
                Assert.Equal(want, have);
            }
            catch (XunitException e)
            {
                throw new XunitException($"{expected.Id}: property {property.Name} differs. {e.Message}");
            }
        }
    }
}
