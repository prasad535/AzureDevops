// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using Hosts.FunctionBase;
using Microsoft.Azure.Functions.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Repositories.Contracts;
using Hosts.JobScheduler;
using Services;
using Services.Contracts;
using Repositories.Contracts.InjectConfig;
using Newtonsoft.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

[assembly: FunctionsStartup(typeof(Startup))]

namespace Hosts.JobScheduler
{
    public class Startup : CommonStartup
    {
        protected override string FunctionName => nameof(JobScheduler);
        protected override string DryRunSettingName => string.Empty;
        protected string JobSchedulerConfigSettingName => "JobScheduler:JobSchedulerConfiguration";

        public override void Configure(IFunctionsHostBuilder builder)
        {
            base.Configure(builder);

            builder.Services.AddOptions<JobSchedulerConfigString>().Configure<IConfiguration>((settings, configuration) =>
            {
                if (!string.IsNullOrEmpty(JobSchedulerConfigSettingName))
                {
                    settings.Value = configuration[JobSchedulerConfigSettingName];
                }

            });

            builder.Services.AddScoped<IJobSchedulerConfig>(services =>
            {
                var jsonString = services.GetService<IOptions<JobSchedulerConfigString>>().Value.Value;
                var jobSchedulerConfig = JsonConvert.DeserializeObject<JobSchedulerConfig>(jsonString);
                return jobSchedulerConfig;
            });

            builder.Services.AddScoped<IRuntimeRetrievalService>(services =>
            {
                return new DefaultRuntimeRetrievalService(services.GetService<IJobSchedulerConfig>().DefaultRuntimeSeconds); // TODO
            });

            builder.Services.AddScoped<IJobSchedulingService>(services =>
            {
                return new JobSchedulingService(
                        services.GetService<IJobSchedulerConfig>(),
                        services.GetService<ISyncJobRepository>(),
                        services.GetService<IRuntimeRetrievalService>(),
                        services.GetService<ILoggingRepository>()
                    );
            });

            builder.Services.AddScoped<IApplicationService>(services =>
            {
                return new ApplicationService(services.GetService<IJobSchedulingService>(), services.GetService<IJobSchedulerConfig>());
            });
        }
    }
}
