#nullable enable

using System.Reflection;

var requested = args.Length == 0 ? "all" : args[0];
var assembly = Assembly.GetExecutingAssembly();
var testTypes = assembly.GetTypes()
    .Where(type => type.IsClass && type.Name.EndsWith("Tests", StringComparison.Ordinal))
    .Where(type =>
    {
        var suite = type.Name[..^"Tests".Length];
        if (requested.Equals("db", StringComparison.OrdinalIgnoreCase))
            return suite.Equals("Db", StringComparison.OrdinalIgnoreCase);
        if (requested.Equals("all", StringComparison.OrdinalIgnoreCase))
            return !suite.Equals("Db", StringComparison.OrdinalIgnoreCase);
        return suite.Equals(requested, StringComparison.OrdinalIgnoreCase);
    })
    .OrderBy(type => type.Name)
    .ToArray();

var tests = testTypes
    .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Where(method => method.GetParameters().Length == 0)
        .Where(method => method.ReturnType == typeof(void) ||
                         method.ReturnType == typeof(Task))
        .Select(method => (Type: type, Method: method)))
    .OrderBy(test => test.Type.Name)
    .ThenBy(test => test.Method.Name)
    .ToArray();

if (testTypes.Length == 0 || tests.Length == 0)
{
    Console.Error.WriteLine($"Unknown or empty test suite: {requested}");
    return 2;
}

var passed = 0;
var failed = 0;

foreach (var test in tests)
{
    var name = $"{test.Type.Name}.{test.Method.Name}";
    try
    {
        var result = test.Method.Invoke(null, null);
        if (result is Task task)
            await task;
        Console.WriteLine($"PASS {name}");
        passed++;
    }
    catch (Exception ex)
    {
        var failure = ex is TargetInvocationException { InnerException: not null }
            ? ex.InnerException
            : ex;
        Console.WriteLine($"FAIL {name}: {failure!.GetType().Name}: {failure.Message}");
        failed++;
    }
}

Console.WriteLine($"PASS={passed} FAIL={failed}");
return failed == 0 ? 0 : 1;
