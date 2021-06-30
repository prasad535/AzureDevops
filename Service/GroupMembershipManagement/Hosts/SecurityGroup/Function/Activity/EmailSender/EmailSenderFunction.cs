// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using Entities;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Extensions.Logging;
using Repositories.Contracts;
using System;
using System.Threading.Tasks;

namespace Hosts.SecurityGroup
{
    public class EmailSenderFunction
    {
        private readonly ILoggingRepository _loggingRepository = null;
        private readonly SGMembershipCalculator _calculator = null;
        private const string SyncDisabledNoValidGroupIds = "SyncDisabledNoValidGroupIds";

        public EmailSenderFunction(ILoggingRepository loggingRepository, SGMembershipCalculator calculator)
        {
            _loggingRepository = loggingRepository ?? throw new ArgumentNullException(nameof(loggingRepository));
            _calculator = calculator ?? throw new ArgumentNullException(nameof(calculator)); ;
        }

        [FunctionName(nameof(EmailSenderFunction))]
        public async Task SendEmailAsync([ActivityTrigger] EmailSenderRequest request, ILogger log)
        {
            if (request.SyncJob != null)
            {
                await _loggingRepository.LogMessageAsync(new LogMessage { Message = $"{nameof(EmailSenderFunction)} function started", RunId = request.RunId });
                await _calculator.SendEmailAsync(request.SyncJob, request.RunId, SyncDisabledNoValidGroupIds, new[] { request.SyncJob.Query });
                await _loggingRepository.LogMessageAsync(new LogMessage { Message = $"{nameof(EmailSenderFunction)} function completed", RunId = request.RunId });
            }
        }
    }
}