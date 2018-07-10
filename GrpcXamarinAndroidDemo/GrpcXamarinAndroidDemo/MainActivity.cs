using Android.App;
using Android.Widget;
using Android.OS;
using Android.Support.V7.App;
using System;
using System.Threading.Tasks;
using System.Reflection;
using System.IO;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Speech.V1;
using Grpc.Core;
using Grpc.Auth;
using System.Threading;

namespace GrpcXamarinAndroidDemo
{
    [Activity(Label = "@string/app_name", Theme = "@style/AppTheme", MainLauncher = true)]
    public class MainActivity : AppCompatActivity
    {
        private Button buttonMain;
        private bool isPlaying = false;
        private CancellationTokenSource cancelPlayingCancellationTokenSource;

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            // Set our view from the "main" layout resource
            SetContentView(Resource.Layout.activity_main);

            buttonMain = FindViewById<Button>(Resource.Id.buttonHello);

            // add click handler
            buttonMain.Click += async (_sender, _args) =>
            {
                string buttonText = null;

                if (isPlaying)
                {
                    // cancel playing
                    cancelPlayingCancellationTokenSource.Cancel();
                    return;
                }

                // guard against exceptions bubbling up
                try
                {
                    // give the user the possibility to cancel playing
                    cancelPlayingCancellationTokenSource = new CancellationTokenSource();
                    // remember button text
                    buttonText = buttonMain.Text;
                    // set text for cancellation
                    buttonMain.Text = "Cancel Playing";

                    // start playing
                    isPlaying = true;
                    await openChannelToGoogleSpeechApiAndStreamWavFileUntil65SecondsException(cancelPlayingCancellationTokenSource.Token);
                }
                catch (Exception)
                {
                    // should not happen
                }
                finally
                {
                    isPlaying = false;
                    // restore button text
                    buttonMain.Text = buttonText;
                }
            };
        }

        private async Task openChannelToGoogleSpeechApiAndStreamWavFileUntil65SecondsException(CancellationToken cancellationToken)
        {
            try
            {
                // required to protect against a System.ArgumentNullException on Xamarin.Android during initialization of type
                // Microsoft.Extensions.PlatformAbstractions.ApplicationEnvironment in the dependency from Google.Api.Gax (see https://github.com/aspnet/PlatformAbstractions/blob/rel/1.1.0/src/Microsoft.Extensions.PlatformAbstractions/ApplicationEnvironment.cs).
                // The field initializer for "ApplicationBasePath" calls GetApplicationBasePath() which calls Path.GetFullPath(basePath)
                // where basePath is AppContext.BaseDirectory which returns null on Xamarin by default (see https://github.com/mono/mono/blob/mono-5.10.0.140/mcs/class/referencesource/mscorlib/system/AppContext/AppContext.cs)
                AppDomain.CurrentDomain.SetData("APP_CONTEXT_BASE_DIRECTORY", Path.DirectorySeparatorChar.ToString());

                var assembly = typeof(MainActivity).GetTypeInfo().Assembly;
                var stream = assembly.GetManifestResourceStream("GrpcXamarinAndroidDemo.speech_auth.json");
                string resultString = null;

                using (var reader = new StreamReader(stream))
                {
                    resultString = reader.ReadToEnd();
                }

                var credential = GoogleCredential.FromJson(resultString);

                if (credential.IsCreateScopedRequired)
                {
                    credential = credential.CreateScoped(new[] { "https://www.googleapis.com/auth/cloud-platform" });
                }

                var channel = new Channel(SpeechClient.DefaultEndpoint.Host,
                    credential.ToChannelCredentials());

                var speech = SpeechClient.Create(channel, new SpeechSettings());

                var streamingCall = speech.StreamingRecognize();
                // Write the initial request with the config.
                await streamingCall.WriteAsync(
                    new StreamingRecognizeRequest()
                    {
                        StreamingConfig = new StreamingRecognitionConfig()
                        {
                            Config = new RecognitionConfig()
                            {
                                Encoding =
                                RecognitionConfig.Types.AudioEncoding.Linear16,
                                SampleRateHertz = 16000,
                                LanguageCode = "en",
                            },
                            InterimResults = true,
                        }
                    });
                // Print responses as they arrive.
                var printResponses = Task.Run(async () =>
                {
                    while (await streamingCall.ResponseStream.MoveNext(
                        default(CancellationToken)))
                    {
                        foreach (var result in streamingCall.ResponseStream
                            .Current.Results)
                        {
                            foreach (var alternative in result.Alternatives)
                            {
                                Console.WriteLine(alternative.Transcript);
                            }
                        }
                    }
                });

                // stream indefinitely
                while (DateTime.UtcNow > DateTime.MinValue && !cancellationToken.IsCancellationRequested)
                {
                    var audioStream = assembly.GetManifestResourceStream("GrpcXamarinAndroidDemo.helloGoogleSpeechAPI.wav");

                    using (var _stream = audioStream)
                    {
                        var buffer = new byte[32 * 1024];
                        int bytesRead;
                        while ((bytesRead = await _stream.ReadAsync(
                            buffer, 0, buffer.Length)) > 0 && !cancellationToken.IsCancellationRequested)
                        {
                            await streamingCall.WriteAsync(
                                new StreamingRecognizeRequest()
                                {
                                    AudioContent = Google.Protobuf.ByteString
                                    .CopyFrom(buffer, 0, bytesRead),
                                });
                            await Task.Delay(500);
                        };
                    }
                }

                await streamingCall.WriteCompleteAsync();
                await printResponses;
            }
            catch (RpcException rpce)
            {
                if (rpce.StatusCode == StatusCode.OutOfRange)
                {
                    Console.WriteLine("65 seconds quota limit reached, this exception is expected");
                }
                else throw;
            }
            catch(System.OperationCanceledException)
            {
                // user canceled operation
            }
        }
    }
}

