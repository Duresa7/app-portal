using System.Reflection;
using System.Runtime.ExceptionServices;

using AppPortal.Client.Services;

namespace AppPortal.Client.Tests;

/// <summary>
/// The demo admin API, signed in, with a way to take over any one call: fail it, hold it open, or see
/// what it was asked. Everything not taken over behaves as the demo does, so a test only writes the
/// part it is about. A proxy rather than a hand-written fake, because the interface has fifty methods.
/// </summary>
public class AdminScriptedApi : DispatchProxy
{
    private readonly Dictionary<string, Func<object?[], object?>> _overrides = [];
    private readonly List<(string Method, object?[] Args)> _calls = [];
    private readonly Lock _gate = new();
    private DemoAdminApiClient _demo = null!;

    public DemoAdminApiClient Demo => _demo;

    public static (IAdminApiClient Api, AdminScriptedApi Script) Create()
    {
        var api = Create<IAdminApiClient, AdminScriptedApi>();
        var script = (AdminScriptedApi)(object)api;
        script._demo = new DemoAdminApiClient();
        script._demo.Token = script._demo.SignInAsync(DemoAdminApiClient.DemoUsername, DemoAdminApiClient.DemoPassword, null, CancellationToken.None)
            .GetAwaiter().GetResult().Token;
        return (api, script);
    }

    /// <summary>Takes over the method with this name. The function gets the arguments and returns what the method returns.</summary>
    public void On(string method, Func<object?[], object?> handler)
    {
        lock (_gate)
        {
            _overrides[method] = handler;
        }
    }

    public void Reset(string method)
    {
        lock (_gate)
        {
            _overrides.Remove(method);
        }
    }

    /// <summary>
    /// How many times a method was called. Locked, because a page's timer calls from the thread pool
    /// in a test, where there is no UI thread to bring it back to.
    /// </summary>
    public int Count(string method)
    {
        lock (_gate)
        {
            return _calls.Count(c => c.Method == method);
        }
    }

    /// <summary>The arguments of every call to a method, oldest first.</summary>
    public IReadOnlyList<object?[]> All(string method)
    {
        lock (_gate)
        {
            return [.. _calls.Where(c => c.Method == method).Select(c => c.Args)];
        }
    }

    /// <summary>The arguments of the last call to a method.</summary>
    public object?[] Last(string method)
    {
        lock (_gate)
        {
            return _calls.Last(c => c.Method == method).Args;
        }
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        args ??= [];
        Func<object?[], object?>? handler;
        lock (_gate)
        {
            _calls.Add((targetMethod.Name, args));
            _overrides.TryGetValue(targetMethod.Name, out handler);
        }

        if (handler is not null)
        {
            return handler(args);
        }

        try
        {
            return targetMethod.Invoke(_demo, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Throw(ex.InnerException);
            throw;
        }
    }
}
