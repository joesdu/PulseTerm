using System.Reactive.Concurrency;
using System.Runtime.CompilerServices;
using ReactiveUI.Builder;

namespace PulseTerm.App.Tests;

internal static class ModuleInit
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        try
        {
            RxAppBuilder.CreateReactiveUIBuilder()
                .WithMainThreadScheduler(CurrentThreadScheduler.Instance)
                .WithCoreServices()
                .BuildApp();
        }
        catch (InvalidOperationException)
        {
            // Already initialized by another path
        }
    }
}
