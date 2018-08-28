﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XAMLator.Server.HttpServer;

namespace XAMLator.Server
{
	/// <summary>
	/// Preview server that process HTTP requests, evaluates them in the <see cref="VM"/>
	/// and preview them with the <see cref="Previewer"/>.
	/// </summary>
	public class PreviewServer : RequestProcessor
	{
		static readonly PreviewServer serverInstance = new PreviewServer ();

		const int PORTS_RANGE = 10;
		DiscoveryBroadcaster broadcaster;
		HttpHost host;
		VM vm;
		TaskScheduler mainScheduler;
		IPreviewer previewer;
		int port;
		bool isRunning;

		internal static PreviewServer Instance => serverInstance;

		PreviewServer ()
		{
			Post ["/xaml"] = HandleNewXaml;
		}

		public static Task<bool> Run (Dictionary<Type, object> viewModelsMapping = null, IPreviewer previewer = null)
		{
			return Instance.RunInternal (viewModelsMapping, previewer);
		}

		internal async Task<bool> RunInternal (Dictionary<Type, object> viewModelsMapping, IPreviewer previewer)
		{
			if (isRunning) {
				return true;
			}
			mainScheduler = TaskScheduler.FromCurrentSynchronizationContext ();
			if (!await StartServer ()) {
				return false;
			}
			broadcaster = new DiscoveryBroadcaster (port);
			if (viewModelsMapping == null) {
				viewModelsMapping = new Dictionary<Type, object> ();
			}
			if (previewer == null) {
				previewer = new Previewer (viewModelsMapping);
			}
			this.previewer = previewer;
			vm = new VM ();
			isRunning = true;
			return true;
		}

		async Task<bool> StartServer ()
		{
			port = Constants.DEFAULT_PORT;
			for (int i = 0; i < PORTS_RANGE; i++) {
				try {
					host = new HttpHost ($"http://+:{port}/", this);
					await host.StartListening ();
					break;
				} catch (Exception ex) {
					if (ex is SocketException || ex is HttpListenerException) {
						host = null;
						port++;
						Log.Exception (ex);
					} else {
						throw;
					}
				}
			}
			if (host == null) {
				Log.Error ("Http server couldn't be started");
				return false;
			}
			return true;
		}

		async Task<HttpResponse> HandleNewXaml (HttpRequest request)
		{
			JsonHttpResponse response = new JsonHttpResponse ();

			StreamReader sr = new StreamReader (request.Body, Encoding.UTF8);
			string json = await sr.ReadToEndAsync ();
			EvalRequest evalRequest = Serializer.DeserializeJson<EvalRequest> (json);
			EvalResponse evalResponse = new EvalResponse ();
			response.Data = evalResponse;
			EvalResult result = null;
			try {
				result = vm.Eval (evalRequest, mainScheduler, CancellationToken.None);
				evalResponse.Messages = result.Messages;
				evalResponse.Duration = result.Duration;
				Log.Information ($"Visualizing result {result.Result}");
				if (result.HasResult) {
					var tcs = new TaskCompletionSource<bool> ();
					Xamarin.Forms.Device.BeginInvokeOnMainThread (async () => {
						try {
							await previewer.Preview (result);
							tcs.SetResult (true);
						} catch (Exception ex) {
							await previewer.NotifyError (new ErrorViewModel ("Oh no! An exception!", ex));
							tcs.SetException (ex);
						}
					});
					await tcs.Task;
				} else {
					Xamarin.Forms.Device.BeginInvokeOnMainThread (async () => {
						await previewer.NotifyError (new ErrorViewModel ("Oh no! An evaluation error!", result));
					});
				}
				return response;
			} catch (Exception ex) {
				Log.Exception (ex);
				response.StatusCode = HttpStatusCode.InternalServerError;
				return response;
			}
		}
	}
}
