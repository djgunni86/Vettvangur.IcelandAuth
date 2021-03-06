using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;

namespace Vettvangur.IcelandAuth
{
    /// <summary>
    /// Process SAML messages received from Ísland.is authentication service
    /// </summary>
    public partial class IcelandAuthService
    {
        /// <summary>
        /// SSN of SAML Signer(Þjóðskrá)
        /// </summary>
        protected const string SignerSSN = "6503760649";
        /// <summary>
        /// Name of issuing CA
        /// </summary>
        protected const string IssuerName = "Traustur bunadur";
        /// <summary>
        /// SSN of issuing CA (Auðkenni)
        /// </summary>
        protected const string IssuerSSN = "5210002790";

        private IEnumerable<string> _authentication;
        /// <summary>
        /// Possible values include:
        /// "Rafræn skilríki"
        /// "Rafræn símaskilríki"
        /// "Íslykill"
        /// </summary>
        public virtual IEnumerable<string> Authentication 
        {
            get => _authentication; 
            set
            {
                if (value != null)
                {
                    _authentication = value.Select(x => x.Trim(' '));
                }
            }
        }

        /// <summary>
        /// Audience will most likely be the sites host name.
        /// F.x. Vettvangur demo is icelandauth.vettvangur.is
        /// </summary>
        public virtual string Audience { get; set; }

        /// <summary>
        /// SAML response url destination. F.x. https://icelandauth.vettvangur.is/umbraco/surface/icelandauth/login
        /// </summary>
        public virtual string Destination { get; set; }

        /// <summary>
        /// The SSN used for contract with Ísland.is. F.x 5208130550
        /// </summary>
        public virtual string DestinationSSN { get; set; }

        /// <summary>
        /// Unique identifier for this contract with Ísland.is in Guid format
        /// Not always included in Saml attributes
        /// </summary>
        public virtual string AuthID { get; set; }

        /// <summary>
        /// Check if the users IP matches the one seen at authentication.
        /// Not always a reliable check, breaks when site is hosted on internal network
        /// Default true
        /// </summary>
        public virtual bool VerifyIPAddress { get; set; }

        /// <summary>
        /// Take care when enabling this setting, sensitive data will be logged.
        /// Never enable in production!
        /// </summary>
        public virtual bool LogSamlResponse { get; set; }

        readonly protected ILogger Logger;

#if NETFRAMEWORK
        /// <summary>
        /// .NET Framework constructor, reads key/values from <see cref="ConfigurationManager.AppSettings"/>.
        /// </summary>
        /// <param name="logger"></param>
        public IcelandAuthService(ILogger logger = null)
        {
            Logger = logger;

            Audience = ConfigurationManager.AppSettings["IcelandAuth.Audience"];
            Destination = ConfigurationManager.AppSettings["IcelandAuth.Destination"];
            DestinationSSN = ConfigurationManager.AppSettings["IcelandAuth.DestinationSSN"];
            //AuthID = ConfigurationManager.AppSettings["IcelandAuth.AuthID"];
            Authentication = string.IsNullOrEmpty(ConfigurationManager.AppSettings["IcelandAuth.Authentication"])
                ? null
                : ConfigurationManager.AppSettings["IcelandAuth.Authentication"].Split(',');
            VerifyIPAddress = bool.TryParse(ConfigurationManager.AppSettings["IcelandAuth.VerifyIPAddress"], out var verifyIpAddress)
                ? verifyIpAddress
                : true;

            bool.TryParse(ConfigurationManager.AppSettings["IcelandAuth.LogSamlResponse"], out var logSamlResponse);
            LogSamlResponse = logSamlResponse;
        }
#endif

        /// <summary>
        /// .NET Core constructor
        /// </summary>
        /// <param name="logger"></param>
        public IcelandAuthService(
            IConfiguration configuration,
            ILogger logger = null
        )
        {
            Logger = logger;

            Audience = configuration["IcelandAuth:Audience"];
            Destination = configuration["IcelandAuth:Destination"];
            DestinationSSN = configuration["IcelandAuth:DestinationSSN"];
            //AuthID = configuration["IcelandAuth:AuthID"];
            Authentication = string.IsNullOrEmpty(configuration["IcelandAuth.Authentication"])
                ? null
                : configuration["IcelandAuth.Authentication"].Split(',');
            VerifyIPAddress = bool.TryParse(configuration["IcelandAuth:VerifyIPAddress"], out var verifyIpAddress)
                ? verifyIpAddress
                : true;

            bool.TryParse(configuration["IcelandAuth:LogSamlResponse"], out var logSamlResponse);
            LogSamlResponse = logSamlResponse;
        }

        /// <summary>
        /// Originally based on C# sample provided by Ísland.is
        /// </summary>
        /// <param name="token"></param>
        /// <param name="ipAddress">
        /// Verify ip address in SAML document matches, 
        /// can be disabled with IcelandAuth.VerifyIPAddress appSetting
        /// </param>
        /// <returns></returns>
        public virtual SamlLogin VerifySaml(
            string token,
            string ipAddress)
        {
            Logger?.LogDebug("Verifying Saml");

            var login = new SamlLogin();

            if (string.IsNullOrEmpty(token))
            {
                Logger?.LogWarning("Null or empty token string");
                return login;
            }

            var doc = ParseToken(token);
            if (doc == null)
            {
                return login;
            }

            var signedXml = new SignedXml(doc);
            
            // Retrieve signature
            XmlElement signedInfo = doc["Response"]?["Signature"];
            var certText = doc["Response"]?["Signature"]?["KeyInfo"]?["X509Data"]?["X509Certificate"]?.InnerText;
            if (certText == null)
            {
                Logger?.LogWarning("Invalid SAML response format");
                return login;
            }

            signedXml.LoadXml(signedInfo);
            byte[] certData = Encoding.UTF8.GetBytes(certText);

            VerifySignature(login, signedXml, certData);

            DateTime nowTime = DateTime.UtcNow;
            // Retrieve time from conditions and compare
            XmlElement conditions = doc["Response"]?["Assertion"]?["Conditions"];
            if (conditions?.Attributes["NotBefore"]?.Value == null
            || conditions?.Attributes["NotOnOrAfter"].Value == null)
            {
                Logger?.LogWarning("Invalid SAML response format");
                return login;
            }

            DateTime fromTime =
                DateTime.Parse(conditions.Attributes["NotBefore"].Value);
            DateTime toTime =
                DateTime.Parse(conditions.Attributes["NotOnOrAfter"].Value);

            if (conditions["AudienceRestriction"]?["Audience"]?.InnerText
                .Equals(Audience, StringComparison.InvariantCultureIgnoreCase) == true)
            {
                login.AudienceOk = true;
            }
            else
            {
                Logger?.LogWarning($"Audience mismatch, received {conditions["AudienceRestriction"]?["Audience"]?.InnerText}");
            }

            if (!string.IsNullOrEmpty(Destination))
            {
                var destination = doc.DocumentElement.Attributes["Destination"].Value;

                if (Destination.Equals(destination, StringComparison.InvariantCultureIgnoreCase))
                {
                    login.DestinationOk = true;
                }
                else
                {
                    Logger?.LogWarning("Destination mismatch, received " + destination);
                }
            }
            else
            {
                login.DestinationOk = true;
            }

            if (nowTime > fromTime && toTime > nowTime)
            {
                login.TimeOk = true;
            }
            else if (nowTime < fromTime)
            {
                Logger?.LogWarning("From time has not passed yet.");
            }
            else if (nowTime > toTime)
            {
                Logger?.LogInformation("Too much time has passed.");
            }

            Logger?.LogDebug("Timestamp verified");

            // Verify ip address and authentication method if provided
            XmlNodeList attrList = doc["Response"]["Assertion"]["AttributeStatement"]?.ChildNodes;

            if (attrList?.Count > 0)
            {
                foreach (XmlNode attrNode in attrList)
                {
                    login.Attributes.Add(new IcelandAuthAttribute
                    {
                        Format = attrNode.Attributes["NameFormat"].Value,
                        Name = attrNode.Attributes["Name"].Value,
                        FriendlyName = attrNode.Attributes["FriendlyName"].Value,
                        Value = attrNode.FirstChild.InnerText
                    });
                }

                // IPAddress
                if (VerifyIPAddress && !string.IsNullOrEmpty(ipAddress))
                {
                    var ipAddressAttr = login.Attributes.First(x => x.Name == "IPAddress");
                    login.IpOk = ipAddressAttr.Value.Equals(ipAddress);

                    if (!login.IpOk)
                    {
                        Logger?.LogWarning($"IP Address mismatch, received {ipAddress} but read {ipAddressAttr.Value} from Saml");
                    }
                }
                else
                {
                    login.IpOk = true;
                }

                // Authentication method used, f.x. phone certificate.
                var authenticationResp = login.Attributes.First(x => x.Name == "Authentication").Value;
                login.Authentication = authenticationResp;
                if (Authentication?.Any() == true)
                {
                    login.AuthMethodOk = Authentication.Contains(authenticationResp);

                    if (!login.AuthMethodOk)
                    {
                        Logger?.LogInformation($"Authentication method not OK, received {authenticationResp}");
                    }
                }
                else
                {
                    login.AuthMethodOk = true;
                }

                var authIdResp = login.Attributes.FirstOrDefault(x => x.Name == "AuthID")?.Value;
                if (!string.IsNullOrEmpty(AuthID) && !string.IsNullOrEmpty(authIdResp))
                {
                    if (AuthID == authIdResp)
                    {
                        login.AuthIdOk = true;
                    }
                    else
                    {
                        Logger?.LogWarning("AuthId mismatch, received " + authIdResp);
                    }
                }
                else
                {
                    login.AuthIdOk = true;
                }

                var destSsnResp = login.Attributes.First(x => x.Name == "DestinationSSN").Value;
                if (!string.IsNullOrEmpty(DestinationSSN))
                {
                    if (DestinationSSN == destSsnResp)
                    {
                        login.DestinationSsnOk = true;
                    }
                    else
                    {
                        Logger?.LogWarning("DestinationSSN mismatch, received " + destSsnResp);
                    }
                }
                else
                {
                    login.DestinationSsnOk = true;
                }

                login.UserSSN = login.Attributes.FirstOrDefault(x => x.Name == "UserSSN")?.Value;

                login.Name = login.Attributes.FirstOrDefault(x => x.Name == "Name")?.Value;

                login.OnbehalfRight = login.Attributes.FirstOrDefault(i => i.Name == "BehalfRight")?.Value;
                login.OnBehalfName = login.Attributes.FirstOrDefault(i => i.Name == "OnBehalfName")?.Value;
                login.OnbehalfSSN = login.Attributes.FirstOrDefault(i => i.Name == "OnBehalfUserSSN")?.Value;
                login.OnbehalfValue = login.Attributes.FirstOrDefault(i => i.Name == "BehalfValue")?.Value;

                var behalfValidityAttr = login.Attributes.FirstOrDefault(i => i.Name == "BehalfValidity");
                if (DateTime.TryParse(behalfValidityAttr?.Value, out var val))
                {
                    login.OnbehalfValidity = val;
                }

                Logger?.LogDebug("Attributes read");
            }
            else
            {
                Logger?.LogWarning("No Attributes found");
            }

            if (LogSamlResponse)
            {
                using (var stringWriter = new StringWriter())
                using (var xmlTextWriter = XmlWriter.Create(stringWriter))
                {
                    doc.WriteTo(xmlTextWriter);
                    xmlTextWriter.Flush();

                    Logger?.LogDebug("SAML Response: \r\n" + stringWriter.GetStringBuilder().ToString());
                }
            }

            if (login.Valid)
            {
                Logger?.LogInformation("Authentication valid");
            }
            else
            {
                Logger?.LogInformation("Authentication invalid");
            }

            return login;
        }

        protected virtual XmlDocument ParseToken(string token)
        {
            byte[] data;
            try
            {
                data = Convert.FromBase64String(token);
            }
            catch (FormatException ex)
            {
                Logger?.LogWarning(ex, "Invalid SAML response format");
                return null;
            }

            string decodedString;
            try
            {
                decodedString = Encoding.UTF8.GetString(data);
            }
            catch (ArgumentException ex)
            {
                Logger?.LogWarning(ex, "Invalid SAML response format");
                return null;
            }


            var doc = new XmlDocument
            {
                PreserveWhitespace = true
            };
            try
            {
                doc.LoadXml(decodedString);
            }
            catch (XmlException ex)
            {
                Logger?.LogWarning(ex, "Invalid SAML response format");
                return null;
            }

            Logger?.LogDebug("Parsed SAML");


            return doc;
        }

        protected virtual void VerifySignature(SamlLogin login, SignedXml signedXml, byte[] certData)
        {
            try
            {
                using (X509Certificate2 cert = new X509Certificate2(certData))
                {
                    // Verify signature
                    login.SignatureOk = signedXml.CheckSignature(cert, false);

                    var issuerComponents = ADUtils.GetDNComponents(cert.Issuer);
                    var subjectComponents = ADUtils.GetDNComponents(cert.Subject);
                    var issuerNameComponent = issuerComponents.FirstOrDefault(x => x.Name == "CN");
                    var issuerSerialComponent = issuerComponents.FirstOrDefault(x => x.Name == "SERIALNUMBER");
                    var subjectSerialComponent = subjectComponents.FirstOrDefault(x => x.Name == "SERIALNUMBER");

                    // default(<struct>) is never null, will also never match our constant values.
                    if (issuerNameComponent.Value == IssuerName
                    && issuerSerialComponent.Value == IssuerSSN
                    && subjectSerialComponent.Value == SignerSSN)
                    {
                        login.CertOk = true;
                        Logger?.LogDebug("Certificate verified");
                    }
                }
            }
            // Invalid certificate, continue on for further logging but validation has failed at this point.
            catch (System.Security.Cryptography.CryptographicException) { }
            finally
            {
                if (!login.SignatureOk)
                {
                    Logger?.LogWarning("Signature error, possible forgery attempt");
                }
                if (!login.CertOk)
                {
                    Logger?.LogWarning("Certificate error, possible forgery attempt");
                }
            }
        }
    }
}
