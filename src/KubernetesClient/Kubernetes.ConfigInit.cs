using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using k8s.Exceptions;
using k8s.Models;
using Microsoft.Rest;
using Newtonsoft.Json;

namespace k8s
{
    public partial class Kubernetes
    {
        /// <summary>
        ///     Initializes a new instance of the <see cref="Kubernetes" /> class.
        /// </summary>
        /// <param name='config'>
        ///     The kube config to use.
        /// </param>
        /// <param name="httpClient">
        ///     The <see cref="HttpClient" /> to use for all requests.
        /// </param>
        public Kubernetes(KubernetesClientConfiguration config, HttpClient httpClient) : this(config, httpClient, false)
        {
        }

        /// <summary>
        ///     Initializes a new instance of the <see cref="Kubernetes" /> class.
        /// </summary>
        /// <param name='config'>
        ///     The kube config to use.
        /// </param>
        /// <param name="httpClient">
        ///     The <see cref="HttpClient" /> to use for all requests.
        /// </param>
        /// <param name="disposeHttpClient">
        ///     Whether or not the <see cref="Kubernetes"/> object should own the lifetime of <paramref name="httpClient"/>.
        /// </param>
        public Kubernetes(KubernetesClientConfiguration config, HttpClient httpClient, bool disposeHttpClient) : this(httpClient, disposeHttpClient)
        {
            ValidateConfig(config);
            this.config = config;
            SetCredentials();
        }

        /// <summary>
        ///     Initializes a new instance of the <see cref="Kubernetes" /> class.
        /// </summary>
        /// <param name='config'>
        ///     The kube config to use.
        /// </param>
        /// <param name="handlers">
        ///     Optional. The delegating handlers to add to the http client pipeline.
        /// </param>
        public Kubernetes(KubernetesClientConfiguration config, params DelegatingHandler[] handlers)
            : this(handlers)
        {
            ValidateConfig(config);
            this.config = config;
            InitializeFromConfig();
        }

        /// <summary>Gets or sets the <see cref="KubernetesScheme"/> used to map types to their Kubernetes groups, versions, and kinds.
        /// The default is <see cref="KubernetesScheme.Default"/>.
        /// </summary>
        /// <summary>Gets or sets the <see cref="KubernetesScheme"/> used to map types to their Kubernetes groups, version, and kinds.</summary>
        public KubernetesScheme Scheme
        {
            get => _scheme;
            set
            {
                if (value == null) throw new ArgumentNullException(nameof(Scheme));
                _scheme = value;
            }
        }


        private void ValidateConfig(KubernetesClientConfiguration config)
        {
            if (config == null)
            {
                throw new KubeConfigException("KubeConfig must be provided");
            }

            if (string.IsNullOrWhiteSpace(config.Host))
            {
                throw new KubeConfigException("Host url must be set");
            }

            try
            {
                BaseUri = new Uri(config.Host);
            }
            catch (UriFormatException e)
            {
                throw new KubeConfigException("Bad host url", e);
            }
        }

        private void InitializeFromConfig()
        {
            if (BaseUri.Scheme == "https")
            {
                if (config.SkipTlsVerify)
                {
#if NET452
                    ((WebRequestHandler) HttpClientHandler).ServerCertificateValidationCallback =
                        (sender, certificate, chain, sslPolicyErrors) => true;
#elif XAMARINIOS1_0 || MONOANDROID8_1
                    System.Net.ServicePointManager.ServerCertificateValidationCallback += (sender, certificate, chain, sslPolicyErrors) =>
                    {
                        return true;
                    };
#else
                    HttpClientHandler.ServerCertificateCustomValidationCallback =
                        (sender, certificate, chain, sslPolicyErrors) => true;
#endif
                }
                else
                {
                    if (config.SslCaCerts == null)
                    {
                        throw new KubeConfigException("A CA must be set when SkipTlsVerify === false");
                    }
#if NET452
                    ((WebRequestHandler) HttpClientHandler).ServerCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) =>
                    {
                        return Kubernetes.CertificateValidationCallBack(sender, config.SslCaCerts, certificate, chain, sslPolicyErrors);
                    };
#elif XAMARINIOS1_0
                    System.Net.ServicePointManager.ServerCertificateValidationCallback += (sender, certificate, chain, sslPolicyErrors) =>
                    {
                        var cert = new X509Certificate2(certificate);
                        return Kubernetes.CertificateValidationCallBack(sender, config.SslCaCerts, cert, chain, sslPolicyErrors);
                    };
#elif MONOANDROID8_1
                    var certList = new System.Collections.Generic.List<Java.Security.Cert.Certificate>();

                    foreach (X509Certificate2 caCert in config.SslCaCerts)
                    {
                        using (var certStream = new System.IO.MemoryStream(caCert.RawData))
                        {
                            Java.Security.Cert.Certificate cert = Java.Security.Cert.CertificateFactory.GetInstance("X509").GenerateCertificate(certStream);

                            certList.Add(cert);
                        }
                    }

                    var handler = (Xamarin.Android.Net.AndroidClientHandler)this.HttpClientHandler;

                    handler.TrustedCerts = certList;
#else
                    HttpClientHandler.ServerCertificateCustomValidationCallback = (sender, certificate, chain, sslPolicyErrors) =>
                    {
                        return Kubernetes.CertificateValidationCallBack(sender, config.SslCaCerts, certificate, chain, sslPolicyErrors);
                    };
#endif
                }
            }

            // set credentials for the kubernetes client
            SetCredentials();
            config.AddCertificates(HttpClientHandler);
        }

        partial void CustomInitialize()
        {
#if NET452 
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
#endif
            AppendDelegatingHandler<WatcherDelegatingHandler>();
            DeserializationSettings.Converters.Add(new V1Status.V1StatusObjectViewConverter());
        }

        private void AppendDelegatingHandler<T>() where T : DelegatingHandler, new()
        {
            var cur = FirstMessageHandler as DelegatingHandler;

            while (cur != null)
            {
                var next = cur.InnerHandler as DelegatingHandler;

                if (next == null)
                {
                    // last one
                    // append watcher handler between to last handler
                    cur.InnerHandler = new T
                    {
                        InnerHandler = cur.InnerHandler
                    };
                    break;
                }

                cur = next;
            }
        }

        /// <summary>
        ///     Set credentials for the Client based on the config
        /// </summary>
        private void SetCredentials()
        {
            Credentials = CreateCredentials(config);
        }

        internal readonly KubernetesClientConfiguration config;
        private KubernetesScheme _scheme = KubernetesScheme.Default;

        /// <summary>
        ///     SSl Cert Validation Callback
        /// </summary>
        /// <param name="sender">sender</param>
        /// <param name="certificate">client certificate</param>
        /// <param name="chain">chain</param>
        /// <param name="sslPolicyErrors">ssl policy errors</param>
        /// <returns>true if valid cert</returns>
        [SuppressMessage("Microsoft.Usage", "CA1801:ReviewUnusedParameters", Justification = "Unused by design")]
        public static bool CertificateValidationCallBack(
            object sender,
            X509Certificate2Collection caCerts,
            X509Certificate certificate,
            X509Chain chain,
            SslPolicyErrors sslPolicyErrors)
        {
            // If the certificate is a valid, signed certificate, return true.
            if (sslPolicyErrors == SslPolicyErrors.None)
            {
                return true;
            }

            // If there are errors in the certificate chain, look at each error to determine the cause.
            if ((sslPolicyErrors & SslPolicyErrors.RemoteCertificateChainErrors) != 0)
            {
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

                // Added our trusted certificates to the chain
                //
                chain.ChainPolicy.ExtraStore.AddRange(caCerts);

                chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
                var isValid = chain.Build((X509Certificate2)certificate);

                var isTrusted = false;

                var rootCert = chain.ChainElements[chain.ChainElements.Count - 1].Certificate;

                // Make sure that one of our trusted certs exists in the chain provided by the server.
                //
                foreach (var cert in caCerts)
                {
                    if (rootCert.RawData.SequenceEqual(cert.RawData))
                    {
                        isTrusted = true;
                        break;
                    }
                }

                return isValid && isTrusted;
            }

            // In all other cases, return false.
            return false;
        }

        /// <summary>Creates the JSON serializer settings used for serializing request bodies and deserializing responses.</summary>
        public static JsonSerializerSettings CreateSerializerSettings()
        {
            var settings = new JsonSerializerSettings() { NullValueHandling = NullValueHandling.Ignore };
            settings.Converters.Add(new Newtonsoft.Json.Converters.StringEnumConverter());
            return settings;
        }

        /// <summary>Creates <see cref="ServiceClientCredentials"/> from a Kubernetes configuration, or returns null if the configuration
        /// contains no credentials of that type.
        /// </summary>
        internal static ServiceClientCredentials CreateCredentials(KubernetesClientConfiguration config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (!string.IsNullOrEmpty(config.AccessToken))
            {
                return new TokenCredentials(config.AccessToken);
            }
            else if (!string.IsNullOrEmpty(config.Username))
            {
                return new BasicAuthenticationCredentials() { UserName = config.Username, Password = config.Password };
            }
            return null;
        }

        /// <summary>Gets the <see cref="JsonSerializerSettings"/> used to serialize and deserialize Kubernetes objects.</summary>
        internal static readonly JsonSerializerSettings DefaultJsonSettings = CreateSerializerSettings();
    }
}
