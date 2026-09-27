using Soenneker.OpenApi.Converters.Meta.Registrars;
using Soenneker.Kiota.Util.Registrars;
using Soenneker.OpenApi.Fixer.Registrars;
using Microsoft.Extensions.DependencyInjection;
using Soenneker.Managers.Runners.Registrars;
using Soenneker.Instagram.Runners.OpenApiClient.Utils;
using Soenneker.Instagram.Runners.OpenApiClient.Utils.Abstract;

namespace Soenneker.Instagram.Runners.OpenApiClient;

/// <summary>
/// Console type startup
/// </summary>
public static class Startup
{
    // This method gets called by the runtime. Use this method to add services to the container.
    public static void ConfigureServices(IServiceCollection services)
    {
        services.SetupIoC();
    }

    public static IServiceCollection SetupIoC(this IServiceCollection services)
    {
        services.AddHostedService<ConsoleHostedService>()
                .AddSingleton<IFileOperationsUtil, FileOperationsUtil>()
                .AddRunnersManagerAsSingleton()
                .AddMetaOpenApiConverterAsSingleton()
                .AddKiotaUtilAsSingleton()
                .AddOpenApiFixerAsSingleton();

        return services;
    }
}
