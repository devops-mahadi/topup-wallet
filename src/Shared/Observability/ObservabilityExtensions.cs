using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Shared.Observability;

/// <summary>
/// One place to wire OpenTelemetry (traces + metrics + logs) for every service,
/// exporting over OTLP. NO backend is bundled — point OTEL_EXPORTER_OTLP_ENDPOINT
/// at whatever collector/backend you run later (Jaeger, Tempo, Prometheus via the
/// OTel Collector, Datadog, Honeycomb…). If no endpoint is set the SDK is still
/// active (useful in tests/dev) and simply has nowhere to export, so instrumenting
/// here costs nothing until a backend is attached.
///
/// Why OTLP specifically: it's the vendor-neutral wire format, so the app is never
/// coupled to a particular observability vendor — swap backends without code change.
///
/// Trace context propagates across HTTP and (with the MassTransit instrumentation
/// each service adds) across RabbitMQ, so a single top-up can be followed as one
/// distributed trace through TopUp -> bus -> Wallet -> external gateway.
/// </summary>
public static class ObservabilityExtensions
{
    public static IServiceCollection AddObservability(
        this IServiceCollection services,
        IConfiguration config,
        string serviceName,
        Action<TracerProviderBuilder>? configureTracing = null,
        Action<MeterProviderBuilder>? configureMetrics = null)
    {
        var otlpEndpoint = config["OTEL_EXPORTER_OTLP_ENDPOINT"]
                           ?? Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");

        // Dev toggle: OTEL_CONSOLE=true prints traces + metrics + logs straight to
        // stdout (container logs) so you can SEE telemetry locally with no backend.
        // Noisy — dev only, off by default.
        var console = string.Equals(
            config["OTEL_CONSOLE"] ?? Environment.GetEnvironmentVariable("OTEL_CONSOLE"),
            "true", StringComparison.OrdinalIgnoreCase);

        var resource = ResourceBuilder.CreateDefault()
            .AddService(serviceName: serviceName,
                serviceVersion: typeof(ObservabilityExtensions).Assembly.GetName().Version?.ToString() ?? "1.0.0");

        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName))
            .WithTracing(t =>
            {
                t.AddAspNetCoreInstrumentation()   // incoming HTTP spans
                 .AddHttpClientInstrumentation();  // outgoing HTTP spans (e.g. operator gateway)
                configureTracing?.Invoke(t);       // service-specific: EF, Redis, MassTransit
                if (otlpEndpoint is not null) t.AddOtlpExporter();
                if (console) t.AddConsoleExporter();
            })
            .WithMetrics(m =>
            {
                m.AddAspNetCoreInstrumentation()
                 .AddHttpClientInstrumentation()
                 .AddRuntimeInstrumentation();      // GC, thread pool, etc.
                configureMetrics?.Invoke(m);
                if (otlpEndpoint is not null) m.AddOtlpExporter();
                if (console) m.AddConsoleExporter();
            });

        // Structured logs through the same pipeline, with trace/span ids attached
        // so a log line can be correlated to its trace.
        services.AddLogging(lb => lb.AddOpenTelemetry(o =>
        {
            o.SetResourceBuilder(resource);
            o.IncludeScopes = true;
            o.IncludeFormattedMessage = true;
            if (otlpEndpoint is not null) o.AddOtlpExporter();
            if (console) o.AddConsoleExporter();
        }));

        return services;
    }
}
