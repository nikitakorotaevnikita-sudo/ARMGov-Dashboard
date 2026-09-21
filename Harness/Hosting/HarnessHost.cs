#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ArmGov.Harness.Hosting;

public static class HarnessHost
{
    public static Func<IModelProvider>? TestModelProviderFactory
    {
        get => Program.TestModelProviderFactory;
        set => Program.TestModelProviderFactory = value;
    }

    public static Func<IEmployeeResolver>? TestEmployeeResolverFactory
    {
        get => Program.TestEmployeeResolverFactory;
        set => Program.TestEmployeeResolverFactory = value;
    }

    public static Func<IQueryExecutor>? TestQueryExecutorFactory
    {
        get => Program.TestQueryExecutorFactory;
        set => Program.TestQueryExecutorFactory = value;
    }

    public static Func<IAnalysisRunner>? TestAnalysisRunnerFactory
    {
        get => Program.TestAnalysisRunnerFactory;
        set => Program.TestAnalysisRunnerFactory = value;
    }

    public static Func<bool>? TestAnalyticsEnabled
    {
        get => Program.TestAnalyticsEnabled;
        set => Program.TestAnalyticsEnabled = value;
    }

    public static string LlmToken
    {
        get => Program.Conf.Llm.Token;
        set => Program.Conf.Llm.Token = value;
    }

    public static string LlmModel
    {
        get => Program.Conf.Llm.Model;
        set => Program.Conf.Llm.Model = value;
    }

    public static bool AnalyticsEnabled
    {
        get => Program.Conf.AnalyticsEnabled;
        set => Program.Conf.AnalyticsEnabled = value;
    }

    public static string AnalyticsEngine
    {
        get => Program.Conf.Analytics?.Engine ?? "workflow";
        set
        {
            Program.Conf.Analytics ??= new Program.AnalyticsCfg();
            Program.Conf.Analytics.Engine = value;
        }
    }

    public static IReadOnlyCollection<string> StaticAllowlist => Program.StaticFileAllowlist;

    public static string CaptureLlmModel() => Program.HarnessRunSnapshot.Capture().Llm.Model;

    public static Task<AnalysisResponse> RunAnalysisAsync(
        AnalysisRequest request,
        CancellationToken ct) =>
        Program.RunAnalysisAsync(request, ct);
}
