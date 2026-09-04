using System.ComponentModel;
using System.Reflection;
using Gmail.Console.Infrastructure;
using Spectre.Console.Cli;

namespace Gmail.Console.Tests;

/// <summary>
/// Spectre applies a [DefaultValue] through the property's own TypeConverter, and those
/// converters refuse even widening conversions: an int default on a long option throws
/// "Int64Converter cannot convert from System.Int32" the moment the option is left out.
/// </summary>
public class CommandSettingsDefaultsTests
{
    public static TheoryData<string, Type, object> Defaults()
    {
        var data = new TheoryData<string, Type, object>();
        foreach (var type in typeof(GlobalSettings).Assembly.GetTypes())
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetCustomAttribute<CommandOptionAttribute>() is null)
                    continue;
                if (property.GetCustomAttribute<DefaultValueAttribute>()?.Value is not { } value)
                    continue;
                data.Add($"{type.FullName}.{property.Name}", property.PropertyType, value);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Defaults))]
    public void Default_value_matches_the_option_type(string option, Type propertyType, object value)
    {
        var target = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        Assert.True(
            target.IsInstanceOfType(value),
            $"{option} is declared as {target.Name} but its [DefaultValue] is a {value.GetType().Name}.");
    }
}
