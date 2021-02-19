﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Azure.Core;
using Azure.Core.Pipeline;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Azure.Iot.ModelsRepository.Fetchers
{
    /// <summary>
    /// The RemoteModelFetcher is an implementation of IModelFetcher
    /// for supporting http[s] based model content fetching.
    /// </summary>
    internal class RemoteModelFetcher : IModelFetcher
    {
        private readonly HttpPipeline _pipeline;
        private readonly ClientDiagnostics _clientDiagnostics;
        private readonly bool _tryExpanded;

        public RemoteModelFetcher(ClientDiagnostics clientDiagnostics, ModelsRepoClientOptions clientOptions)
        {
            _pipeline = CreatePipeline(clientOptions);
            _tryExpanded = clientOptions.DependencyResolution == DependencyResolutionOption.TryFromExpanded;
            _clientDiagnostics = clientDiagnostics;
        }

        public FetchResult Fetch(string dtmi, Uri repositoryUri, CancellationToken cancellationToken = default)
        {
            using DiagnosticScope scope = _clientDiagnostics.CreateScope("RemoteModelFetcher.Fetch");
            scope.Start();
            try
            {
                Queue<string> work = PrepareWork(dtmi, repositoryUri);

                string remoteFetchError = string.Empty;

                while (work.Count != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string tryContentPath = work.Dequeue();
                    ModelsRepoEventSource.Instance.FetchingModelContent(tryContentPath);

                    try
                    {
                        string content = EvaluatePath(tryContentPath, cancellationToken);
                        return new FetchResult
                        {
                            Definition = content,
                            Path = tryContentPath
                        };
                    }
                    catch (Exception)
                    {
                        remoteFetchError =
                            $"{string.Format(CultureInfo.CurrentCulture, ServiceStrings.GenericResolverError, dtmi)} " +
                            string.Format(CultureInfo.CurrentCulture, StandardStrings.ErrorFetchingModelContent, tryContentPath);
                    }
                }

                throw new RequestFailedException(remoteFetchError);
            }
            catch (Exception ex)
            {
                scope.Failed(ex);
                throw;
            }
        }

        public async Task<FetchResult> FetchAsync(string dtmi, Uri repositoryUri, CancellationToken cancellationToken = default)
        {
            using DiagnosticScope scope = _clientDiagnostics.CreateScope("RemoteModelFetcher.Fetch");
            scope.Start();
            try
            {
                Queue<string> work = PrepareWork(dtmi, repositoryUri);

                string remoteFetchError = string.Empty;

                while (work.Count != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string tryContentPath = work.Dequeue();
                    ModelsRepoEventSource.Instance.FetchingModelContent(tryContentPath);

                    try
                    {
                        string content = await EvaluatePathAsync(tryContentPath, cancellationToken).ConfigureAwait(false);
                        return new FetchResult()
                        {
                            Definition = content,
                            Path = tryContentPath
                        };
                    }
                    catch (Exception)
                    {
                        remoteFetchError =
                            $"{string.Format(CultureInfo.CurrentCulture, ServiceStrings.GenericResolverError, dtmi)} " +
                            string.Format(CultureInfo.CurrentCulture, StandardStrings.ErrorFetchingModelContent, tryContentPath);
                    }
                }

                throw new RequestFailedException(remoteFetchError);
            }
            catch (Exception ex)
            {
                scope.Failed(ex);
                throw;
            }
        }

        private Queue<string> PrepareWork(string dtmi, Uri repositoryUri)
        {
            Queue<string> work = new Queue<string>();

            if (_tryExpanded)
            {
                work.Enqueue(GetPath(dtmi, repositoryUri, true));
            }

            work.Enqueue(GetPath(dtmi, repositoryUri, false));

            return work;
        }

        private static string GetPath(string dtmi, Uri repositoryUri, bool expanded = false)
        {
            string absoluteUri = repositoryUri.AbsoluteUri;
            return DtmiConventions.DtmiToQualifiedPath(dtmi, absoluteUri, expanded);
        }

        private string EvaluatePath(string path, CancellationToken cancellationToken = default)
        {
            using DiagnosticScope scope = _clientDiagnostics.CreateScope("RemoteModelFetcher.EvaluatePath");
            scope.Start();

            try
            {
                using HttpMessage message = CreateGetRequest(path);

                _pipeline.Send(message, cancellationToken);

                switch (message.Response.Status)
                {
                    case 200:
                        {
                            return GetContent(message.Response.ContentStream);
                        }
                    default:
                        throw _clientDiagnostics.CreateRequestFailedException(message.Response);
                }
            }
            catch (Exception ex)
            {
                scope.Failed(ex);
                throw;
            }
        }

        private async Task<string> EvaluatePathAsync(string path, CancellationToken cancellationToken = default)
        {
            using DiagnosticScope scope = _clientDiagnostics.CreateScope("RemoteModelFetcher.EvaluatePath");
            scope.Start();

            try
            {
                using HttpMessage message = CreateGetRequest(path);

                await _pipeline.SendAsync(message, cancellationToken).ConfigureAwait(false);

                switch (message.Response.Status)
                {
                    case 200:
                        {
                            return await GetContentAsync(message.Response.ContentStream, cancellationToken).ConfigureAwait(false);
                        }
                    default:
                        throw _clientDiagnostics.CreateRequestFailedException(message.Response);
                }
            }
            catch (Exception ex)
            {
                scope.Failed(ex);
                throw;
            }
        }

        private HttpMessage CreateGetRequest(string path)
        {
            HttpMessage message = _pipeline.CreateMessage();
            Request request = message.Request;
            request.Method = RequestMethod.Get;
            var uri = new RequestUriBuilder();
            uri.Reset(new Uri(path));
            request.Uri = uri;

            return message;
        }

        private static string GetContent(Stream content)
        {
            using JsonDocument json = JsonDocument.Parse(content);
            JsonElement root = json.RootElement;
            return root.GetRawText();
        }

        private static async Task<string> GetContentAsync(Stream content, CancellationToken cancellationToken)
        {
            using JsonDocument json = await JsonDocument.ParseAsync(content, default, cancellationToken).ConfigureAwait(false);

            JsonElement root = json.RootElement;
            return root.GetRawText();
        }

        private static HttpPipeline CreatePipeline(ModelsRepoClientOptions options)
        {
            return HttpPipelineBuilder.Build(options);
        }
    }
}
