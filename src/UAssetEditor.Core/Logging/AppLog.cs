using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace UAssetEditor.Core.Logging;

/// <summary>
/// Ambient access to the app's logger factory - most of this app's ViewModels are constructed
/// directly with `new` rather than through DI (only <see cref="object"/>-graph roots go through
/// the container - see App.xaml.cs), so threading an ILoggerFactory through every constructor
/// would touch every call site for a cross-cutting concern. <see cref="Initialize"/> is called
/// once at startup; before that (or in a unit test that never calls it), <see cref="For{T}"/>
/// hands back a no-op logger instead of throwing, so logging is always safe to call.
/// </summary>
public static class AppLog
{
    private static ILoggerFactory _factory = NullLoggerFactory.Instance;

    public static void Initialize(ILoggerFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public static ILogger For<T>() => _factory.CreateLogger<T>();
}
