// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using Entities;
using Microsoft.ApplicationInsights;
using Microsoft.Graph;
using Polly;
using Repositories.Contracts;
using Repositories.Contracts.InjectConfig;
using Services.Contracts;
using Services.Entities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Services
{
    public class GraphUpdaterService : IGraphUpdaterService
    {
        private const int NumberOfGraphRetries = 5;
        private const string EmailSubject = "EmailSubject";
        private readonly ILoggingRepository _loggingRepository;
        private readonly TelemetryClient _telemetryClient;
        private readonly IGraphGroupRepository _graphGroupRepository;
        private readonly IMailRepository _mailRepository;
        private readonly IEmailSenderRecipient _emailSenderAndRecipients;
        private readonly ISyncJobRepository _syncJobRepository;
        private readonly bool _isGraphUpdaterDryRunEnabled;
        enum Metric
        {
            SyncComplete,
            SyncJobTimeElapsedSeconds,
            MembersAdded,
            MembersRemoved,
            MembersAddedFromOnboarding,
            MembersRemovedFromOnboarding,
            GraphAddRatePerSecond,
            GraphRemoveRatePerSecond
        };

        public GraphUpdaterService(
                ILoggingRepository loggingRepository,
                TelemetryClient telemetryClient,
                IGraphGroupRepository graphGroupRepository,
                IMailRepository mailRepository,
                IEmailSenderRecipient emailSenderAndRecipients,
                ISyncJobRepository syncJobRepository,
                IDryRunValue dryRun)
        {
            _loggingRepository = loggingRepository ?? throw new ArgumentNullException(nameof(loggingRepository));
            _telemetryClient = telemetryClient ?? throw new ArgumentNullException(nameof(telemetryClient));
            _graphGroupRepository = graphGroupRepository ?? throw new ArgumentNullException(nameof(graphGroupRepository));
            _mailRepository = mailRepository ?? throw new ArgumentNullException(nameof(mailRepository));
            _emailSenderAndRecipients = emailSenderAndRecipients ?? throw new ArgumentNullException(nameof(emailSenderAndRecipients));
            _syncJobRepository = syncJobRepository ?? throw new ArgumentNullException(nameof(syncJobRepository));
            _isGraphUpdaterDryRunEnabled = _loggingRepository.DryRun = dryRun != null ? dryRun.DryRunEnabled : throw new ArgumentNullException(nameof(dryRun));
        }

        public async Task<UsersPageResponse> GetFirstMembersPageAsync(Guid groupId, Guid runId)
        {
            await _loggingRepository.LogMessageAsync(new LogMessage { RunId = runId, Message = $"Reading users from the group with ID {groupId}." });
            var result = await _graphGroupRepository.GetFirstUsersPageAsync(groupId);
            return new UsersPageResponse
            {
                NextPageUrl = result.nextPageUrl,
                Members = result.users,
                NonUserGraphObjects = result.nonUserGraphObjects,
                MembersPage = result.usersFromGroup
            };
        }

        public async Task<UsersPageResponse> GetNextMembersPageAsync(string nextPageUrl, IGroupTransitiveMembersCollectionWithReferencesPage usersFromGroup)
        {
            var result = await _graphGroupRepository.GetNextUsersPageAsync(nextPageUrl, usersFromGroup);
            return new UsersPageResponse
            {
                NextPageUrl = result.nextPageUrl,
                Members = result.users,
                NonUserGraphObjects = result.nonUserGraphObjects,
                MembersPage = result.usersFromGroup
            };
        }

        public async Task<PolicyResult<bool>> GroupExistsAsync(Guid groupId, Guid runId)
        {
            var graphRetryPolicy = Policy.Handle<SocketException>()
                                    .WaitAndRetryAsync(NumberOfGraphRetries, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                   onRetry: async (ex, count) =>
                   {
                       await _loggingRepository.LogMessageAsync(new LogMessage
                       {
                           Message = $"Got a transient SocketException. Retrying. This was try {count} out of {NumberOfGraphRetries}.\n" + ex.ToString(),
                           RunId = runId
                       });
                   });

            return await graphRetryPolicy.ExecuteAndCaptureAsync(() => _graphGroupRepository.GroupExists(groupId));
        }

        public async Task SendEmailAsync(string toEmail, string contentTemplate, string[] additionalContentParams, Guid runId, string ccEmail = null)
        {
            await _mailRepository.SendMailAsync(new EmailMessage
            {
                Subject = EmailSubject,
                Content = contentTemplate,
                SenderAddress = _emailSenderAndRecipients.SenderAddress,
                SenderPassword = _emailSenderAndRecipients.SenderPassword,
                ToEmailAddresses = toEmail,
                CcEmailAddresses = ccEmail,
                AdditionalContentParams = additionalContentParams
            }, runId);
        }

        public async Task UpdateSyncJobStatusAsync(SyncJob job, SyncStatus status, bool isDryRun, Guid runId)
        {
            await _loggingRepository.LogMessageAsync(new LogMessage { Message = $"Set job status to {status}.", RunId = runId });

            var timeElapsedForJob = (DateTime.Now - job.Timestamp).TotalSeconds;
            _telemetryClient.TrackMetric(nameof(Metric.SyncJobTimeElapsedSeconds), timeElapsedForJob);

            var syncCompleteEvent = new Dictionary<string, string>
            {
                { "SyncJobType", job.Type },
                { "Result", status == SyncStatus.Idle ? "Success": "Failure" },
                { "DryRunEnabled", job.IsDryRunEnabled.ToString() }
            };

            _telemetryClient.TrackEvent(nameof(Metric.SyncComplete), syncCompleteEvent);

            var isDryRunSync = job.IsDryRunEnabled || _isGraphUpdaterDryRunEnabled || isDryRun;

            if (isDryRunSync)
                job.DryRunTimeStamp = DateTime.UtcNow;
            else
                job.LastRunTime = DateTime.UtcNow;

            job.RunId = runId;
            job.Enabled = status != SyncStatus.Error;

            await _syncJobRepository.UpdateSyncJobStatusAsync(new[] { job }, status);

            string message = isDryRunSync
                                ? $"Dry Run of a sync to {job.TargetOfficeGroupId} is complete. Membership will not be updated."
                                : $"Syncing to {job.TargetOfficeGroupId} done.";

            await _loggingRepository.LogMessageAsync(new LogMessage { Message = message, RunId = runId });
        }

        public async Task<SyncJob> GetSyncJobAsync(string partitionKey, string rowKey)
        {
            return await _syncJobRepository.GetSyncJobAsync(partitionKey, rowKey);
        }

        public async Task<string> GetGroupNameAsync(Guid groupId)
        {
            return await _graphGroupRepository.GetGroupNameAsync(groupId);
        }

        public async Task<GraphUpdaterStatus> AddUsersToGroupAsync(ICollection<AzureADUser> members, Guid targetGroupId, Guid runId, bool isInitialSync)
        {
            if (_isGraphUpdaterDryRunEnabled)
            {
                return GraphUpdaterStatus.Ok;
            }

            var stopwatch = Stopwatch.StartNew();
            var graphResponse = await _graphGroupRepository.AddUsersToGroup(members, new AzureADGroup { ObjectId = targetGroupId });
            stopwatch.Stop();

            if (isInitialSync)
                _telemetryClient.TrackMetric(nameof(Metric.MembersAddedFromOnboarding), members.Count);
            else
                _telemetryClient.TrackMetric(nameof(Metric.MembersAdded), members.Count);

            await _loggingRepository.LogMessageAsync(new LogMessage
            {
                Message = $"Adding {members.Count} users to group {targetGroupId} complete in {stopwatch.Elapsed.TotalSeconds} seconds. " +
                $"{members.Count / stopwatch.Elapsed.TotalSeconds} users added per second. ",
                RunId = runId
            });

            return graphResponse == ResponseCode.Error ? GraphUpdaterStatus.Error : GraphUpdaterStatus.Ok;
        }

        public async Task<GraphUpdaterStatus> RemoveUsersFromGroupAsync(ICollection<AzureADUser> members, Guid targetGroupId, Guid runId, bool isInitialSync)
        {
            if (_isGraphUpdaterDryRunEnabled)
            {
                return GraphUpdaterStatus.Ok;
            }

            var stopwatch = Stopwatch.StartNew();
            var graphResponse = await _graphGroupRepository.RemoveUsersFromGroup(members, new AzureADGroup { ObjectId = targetGroupId });
            stopwatch.Stop();

            if (isInitialSync)
                _telemetryClient.TrackMetric(nameof(Metric.MembersRemovedFromOnboarding), members.Count);
            else
                _telemetryClient.TrackMetric(nameof(Metric.MembersRemoved), members.Count);

            await _loggingRepository.LogMessageAsync(new LogMessage
            {
                Message = $"Removing {members.Count} users from group {targetGroupId} complete in {stopwatch.Elapsed.TotalSeconds} seconds. " +
                $"{members.Count / stopwatch.Elapsed.TotalSeconds} users removed per second.",
                RunId = runId
            });

            return graphResponse == ResponseCode.Error ? GraphUpdaterStatus.Error : GraphUpdaterStatus.Ok;
        }
    }
}