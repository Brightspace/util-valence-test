using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;

using CommandLine;
using CommandLine.Text;

using D2L.Extensibility.AuthSdk;
using D2L.Extensibility.AuthSdk.Restsharp;

using Newtonsoft.Json;

using RestSharp;

namespace ValenceTest {
	internal class Options {
		[Option( "appId", Required = true,
			HelpText = "Valence Application ID" )]
		public string AppId { get; set; }

		[Option( "appKey", Required = true,
			HelpText = "Valence Application Key" )]
		public string AppKey { get; set; }

		[Option( 'h', "host", Required = true,
			HelpText = "URL for LMS, e.g. https://lms.valence.desire2learn.com. Defaults to http. Set port if needed." )]
		public string Host { get; set; }

		[Option( 'v', "verbose", DefaultValue = false,
			HelpText = "Print extra output" )]
		public bool Verbose { get; set; }

		[Option( 'g', "guess", DefaultValue = false,
			HelpText = "Make a short guess at what the error is if one occurs and print it to stdout" )]
		public bool Guess { get; set; }

		[ParserState]
		public IParserState LastParserState { get; set; }

		[HelpOption]
		public string GetUsage( ) {
			return HelpText.AutoBuild( this, current => HelpText.DefaultParsingErrorsHandler( this, current ) );
		}
	}

	public class VersionsItem {
		public string ProductCode { get; set; }
		public string LatestVersion { get; set; }
		public List<string> SupportedVersions { get; set; }
	}

	internal class Program {
		private const string VERSIONS_ROUTE = "/d2l/api/versions/";

		private static void PrintHeaders( TextWriter output, IRestResponse response ) {
			response.Headers.ToList().ForEach( h => output.WriteLine( Indent( h.Name + ": " + h.Value ) ) );
		}

		private static string Indent( string input ) {
			var sb = new StringBuilder();
			input.Split( new[] {"\r\n", "\n"}, StringSplitOptions.None ).ToList().ForEach( s => sb.Append( "  " + s ) );
			return sb.ToString();
		}

		private static long GetTime( ) {
			return (long) DateTime.UtcNow.Subtract( new DateTime( 1970, 1, 1, 0, 0, 0, DateTimeKind.Utc ) ).TotalMilliseconds;
		}

		private static int Main( string[] args ) {
			var options = new Options();
			try {
				if (!Parser.Default.ParseArguments( args, options )) {
					return -1;
				}
			}
			catch (TargetInvocationException e) {
				Console.Error.Write( options.GetUsage() );
				Console.Error.WriteLine( e.InnerException.InnerException.Message );
				return -2;
			}

			var host = new Uri( options.Host );
			var appContextFactory = new D2LAppContextFactory();
			var appContext = appContextFactory.Create( options.AppId, options.AppKey );
			var userContext = appContext.CreateAnonymousUserContext( new HostSpec( host.Scheme, host.Host, host.Port ) );

			const int MAX_ATTEMPTS = 3;
			var attempts = 0;
			IRestResponse response;
			bool again;
			do {
				if (options.Verbose && attempts != 0) {
					Console.WriteLine( "Making attempt #" + ( attempts + 1 ) );
				}
				var client = new RestClient( host.ToString() );
				var authenticator = new ValenceAuthenticator( userContext );
				var request = new RestRequest( VERSIONS_ROUTE, Method.GET );
				authenticator.Authenticate( client, request );

				response = client.Execute( request );

				again = false;
				if (response.StatusCode == HttpStatusCode.Forbidden && response.Content.StartsWith( "Timestamp out of range" )) {
					var serverTime = 1000*long.Parse( new string( response.Content.SkipWhile( x => !char.IsNumber( x ) ).ToArray() ) );
					userContext.ServerSkewMillis = serverTime - GetTime();
					again = true;
				}

				attempts++;
			} while (again & attempts < MAX_ATTEMPTS);

			if (options.Verbose && attempts == MAX_ATTEMPTS) {
				Console.WriteLine( "Too much timestamp skew, giving up." );
			}


			if (response.StatusCode == HttpStatusCode.OK) {
				try {
					JsonConvert.DeserializeObject<List<VersionsItem>>( response.Content );
				}
				catch (JsonSerializationException e) {
					Console.Error.WriteLine( "Call succeeded but could not deserialize the response." );
					Console.Error.Write( "Error: " );
					Console.Error.WriteLine( Indent( e.Message ) );
					if (!string.IsNullOrEmpty( response.Content )) {
						Console.Error.WriteLine( "Response:" );
						Console.Error.WriteLine( Indent( response.Content ) );
					}
					return -3;
				}
				Console.WriteLine( "Ok" );
				if (options.Verbose) {
					Console.WriteLine( "Response headers:" );
					PrintHeaders( Console.Out, response );
					Console.WriteLine( "Response body:" );
					Console.WriteLine( Indent( response.Content ) );
				}
				return 0;
			}

			if (options.Guess) {
				if (response.StatusCode == HttpStatusCode.Forbidden && response.Content == "Invalid token") {
					Console.WriteLine( "App not synced to LMS or explicitly denied in Manage Extensibility." );
				}
				else if (response.StatusCode == HttpStatusCode.Forbidden && response.Content.StartsWith( "Timestamp out of range" )) {
					Console.WriteLine( "Timestamp skew could not be rectified." );
				}
				else if (!string.IsNullOrEmpty( response.ErrorMessage )) {
					Console.WriteLine( response.ErrorMessage );
				}
				else {
					Console.WriteLine( "Unknown error" );
				}
			}
			else {
				Console.Error.WriteLine( "Failure!" );
				if (response.ErrorMessage != null) {
					Console.Error.WriteLine( "Error: " );
					Console.Error.WriteLine( Indent( response.ErrorMessage ) );
				}
				if (response.StatusDescription != null) {
					Console.Error.WriteLine( "Response status: " );
					Console.Error.WriteLine( Indent( response.StatusDescription ) );
				}
				if (response.Headers != null) {
					PrintHeaders( Console.Error, response );
				}
				if (!String.IsNullOrEmpty( response.Content )) {
					Console.Error.WriteLine( "Response: " );
					Console.Error.WriteLine( Indent( response.Content ) );
				}
			}
			return -4;
		}
	}
}