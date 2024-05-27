﻿//-----------------------------------------------------------------------------
// Filename: DtlsUtils.cs
//
// Description: This class provides useful functions to handle certificate in 
// DTLS-SRTP.
//
// Notes: The webrtc specification provides guidelines for X509 certificate
// management:
// https://www.w3.org/TR/webrtc/#certificate-management
//
// In particular:
// "The explicit certificate management functions provided here are optional. 
// If an application does not provide the certificates configuration option 
// when constructing an RTCPeerConnection a new set of certificates MUST be 
// generated by the user agent. That set MUST include an ECDSA certificate with 
// a private key on the P-256 curve and a signature with a SHA-256 hash."
//
// Based on the above it's likely the safest algorithm to use is ECDSA rather
// than RSA (which will then result in an ECDH rather than DH exchange to
// initialise the SRTP keying material).
// https://www.w3.org/TR/WebCryptoAPI/#algorithms
//
// The recommended ECDSA curves are listed at:
// https://www.w3.org/TR/WebCryptoAPI/#ecdsa
// and are:
// - P-256, also known as secp256r1.
// - P-384, also known as secp384r1.
// - P-521, also known as secp521r1.
//
// TODO: Switch the self-signed certificates generated in this class to use
// ECDSA instead of RSA.
//
// Author(s):
// Rafael Soares (raf.csoares@kyubinteractive.com)
//
// History:
// 01 Jul 2020	Rafael Soares   Created.
//
// License:
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
//-----------------------------------------------------------------------------

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Prng;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities;
using Org.BouncyCastle.Utilities.IO.Pem;
using Org.BouncyCastle.X509;
using SIPSorcery.Sys;
using System.Runtime.CompilerServices;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using Org.BouncyCastle.Tls.Crypto;

namespace SIPSorcery.Net
{
    public class DtlsUtils
    {
        /// <summary>
        /// The key size when generating random keys for self signed certificates.
        /// </summary>
        public const int DEFAULT_KEY_SIZE = 2048;

        private static ILogger logger = SIPSorcery.Sys.Log.Logger;

        public static RTCDtlsFingerprint Fingerprint(TlsCrypto crypto, string hashAlgorithm, X509Certificate2 certificate)
        {
            return Fingerprint(hashAlgorithm, LoadCertificateResource(crypto, certificate));
        }

        public static RTCDtlsFingerprint Fingerprint(string hashAlgorithm, TlsCertificate c)
        {
            if (!IsHashSupported(hashAlgorithm))
            {
                throw new ApplicationException($"Hash algorithm {hashAlgorithm} is not supported for DTLS fingerprints.");
            }

            IDigest digestAlgorithm = DigestUtilities.GetDigest(hashAlgorithm.ToString());
            byte[] der = c.GetEncoded();
            byte[] hash = DigestOf(digestAlgorithm, der);

            return new RTCDtlsFingerprint
            {
                algorithm = digestAlgorithm.AlgorithmName.ToLower(),
                value = hash.HexStr(':')
            };
        }

        public static RTCDtlsFingerprint Fingerprint(Certificate certificateChain)
        {
            var certificate = certificateChain.GetCertificateAt(0);
            return Fingerprint(certificate);
        }

        public static RTCDtlsFingerprint Fingerprint(TlsCrypto crypto, X509Certificate2 certificate)
        {
            return Fingerprint(LoadCertificateResource(crypto, certificate));
        }

        public static RTCDtlsFingerprint Fingerprint(Org.BouncyCastle.X509.X509Certificate certificate)
        {
            var certStruct = X509CertificateStructure.GetInstance(certificate.GetEncoded());
            return Fingerprint(certStruct);
        }

        public static RTCDtlsFingerprint Fingerprint(X509CertificateStructure c)
        {
            IDigest sha256 = DigestUtilities.GetDigest(HashAlgorithmTag.Sha256.ToString());
            byte[] der = c.GetEncoded();
            byte[] sha256Hash = DigestOf(sha256, der);

            return new RTCDtlsFingerprint
            {
                algorithm = sha256.AlgorithmName.ToLower(),
                value = sha256Hash.HexStr(':')
            };
        }
        public static RTCDtlsFingerprint Fingerprint(TlsCertificate c)
        {
            IDigest sha256 = DigestUtilities.GetDigest(HashAlgorithmTag.Sha256.ToString());
            byte[] der = c.GetEncoded();
            byte[] sha256Hash = DigestOf(sha256, der);

            return new RTCDtlsFingerprint
            {
                algorithm = sha256.AlgorithmName.ToLower(),
                value = sha256Hash.HexStr(':')
            };
        }

        public static byte[] DigestOf(IDigest dAlg, byte[] input)
        {
            dAlg.BlockUpdate(input, 0, input.Length);
            byte[] result = new byte[dAlg.GetDigestSize()];
            dAlg.DoFinal(result, 0);
            return result;
        }

        public static TlsCredentialedAgreement LoadAgreementCredentials(TlsContext context,
                Certificate certificate, AsymmetricKeyParameter privateKey)
        {
            return new BcDefaultTlsCredentialedAgreement(context.Crypto as BcTlsCrypto, certificate, privateKey);
        }

        public static TlsCredentialedAgreement LoadAgreementCredentials(TlsContext context,
                string[] certResources, string keyResource)
        {
            Certificate certificate = LoadCertificateChain(context.Crypto, certResources);
            AsymmetricKeyParameter privateKey = LoadPrivateKeyResource(keyResource);
            return LoadAgreementCredentials(context, certificate, privateKey);
        }

        public static TlsCredentialedDecryptor LoadEncryptionCredentials(
                TlsContext context, Certificate certificate, AsymmetricKeyParameter privateKey)
        {

            return new BcDefaultTlsCredentialedDecryptor(context.Crypto as BcTlsCrypto, certificate,
                    privateKey);
        }

        public static TlsCredentialedDecryptor LoadEncryptionCredentials(
                TlsContext context, string[] certResources, string keyResource)
        {
            Certificate certificate = LoadCertificateChain(context.Crypto, certResources);
            AsymmetricKeyParameter privateKey = LoadPrivateKeyResource(keyResource);
            return LoadEncryptionCredentials(context, certificate,
                    privateKey);
        }

        public static TlsCredentialedSigner LoadSignerCredentials(TlsContext context,
                Certificate certificate, AsymmetricKeyParameter privateKey,
                SignatureAndHashAlgorithm signatureAndHashAlgorithm)
        {
            return new BcDefaultTlsCredentialedSigner(new TlsCryptoParameters(context), context.Crypto as BcTlsCrypto, privateKey, certificate, signatureAndHashAlgorithm);
        }

        public static TlsCredentialedSigner LoadSignerCredentials(TlsContext context,
                string[] certResources, string keyResource,
                SignatureAndHashAlgorithm signatureAndHashAlgorithm)
        {
            Certificate certificate = LoadCertificateChain(context.Crypto as BcTlsCrypto, certResources);
            Org.BouncyCastle.Crypto.AsymmetricKeyParameter privateKey = LoadPrivateKeyResource(keyResource);
            return LoadSignerCredentials(context, certificate,
                    privateKey, signatureAndHashAlgorithm);
        }

        public static TlsCredentialedSigner LoadSignerCredentials(TlsContext context, IList<SignatureAndHashAlgorithm> supportedSignatureAlgorithms,
            short signatureAlgorithm, Certificate certificate, AsymmetricKeyParameter privateKey)
        {
            /*
             * TODO Note that this code fails to provide default value for the client supported
             * algorithms if it wasn't sent.
             */

            SignatureAndHashAlgorithm signatureAndHashAlgorithm = null;
            if (supportedSignatureAlgorithms != null)
            {
                foreach (SignatureAndHashAlgorithm alg in supportedSignatureAlgorithms)
                {
                    if (alg.Signature == signatureAlgorithm)
                    {
                        signatureAndHashAlgorithm = alg;
                        break;
                    }
                }

                if (signatureAndHashAlgorithm == null)
                {
                    return null;
                }
            }

            return LoadSignerCredentials(context, certificate, privateKey, signatureAndHashAlgorithm);
        }

        public static TlsCredentialedSigner LoadSignerCredentials(TlsContext context, IList<SignatureAndHashAlgorithm> supportedSignatureAlgorithms,
            byte signatureAlgorithm, string certResource, string keyResource)
        {
            Certificate certificate = LoadCertificateChain(context.Crypto as BcTlsCrypto, new string[] { certResource, "x509-ca.pem" });
            AsymmetricKeyParameter privateKey = LoadPrivateKeyResource(keyResource);

            return LoadSignerCredentials(context, supportedSignatureAlgorithms, signatureAlgorithm, certificate,
                privateKey);
        }

        public static Certificate LoadCertificateChain(TlsCrypto crypto, X509Certificate2[] certificates)
        {
            var chain = new TlsCertificate[certificates.Length];
            for (int i = 0; i < certificates.Length; i++)
            {
                chain[i] = LoadCertificateResource(crypto, certificates[i]);
            }

            return new Certificate(chain);
        }

        public static Certificate LoadCertificateChain(TlsCrypto crypto, X509Certificate2 certificate)
        {
            return LoadCertificateChain(crypto, new X509Certificate2[] { certificate });
        }

        public static Certificate LoadCertificateChain(TlsCrypto crypto, string[] resources)
        {
            TlsCertificate[]
            chain = new TlsCertificate[resources.Length];
            for (int i = 0; i < resources.Length; ++i)
            {
                chain[i] = LoadCertificateResource(crypto, resources[i]);
            }
            return new Certificate(chain);
        }

        public static TlsCertificate LoadCertificateResource(TlsCrypto crypto, X509Certificate2 certificate)
        {
            if (certificate != null)
            {
                var bouncyCertificate = DotNetUtilities.FromX509Certificate(certificate);
                return new BcTlsCertificate(crypto as BcTlsCrypto, X509CertificateStructure.GetInstance(bouncyCertificate.GetEncoded()));
            }
            throw new Exception("'resource' doesn't specify a valid certificate");
        }

        public static TlsCertificate LoadCertificateResource(TlsCrypto crypto, string resource)
        {
            PemObject pem = LoadPemResource(resource);
            if (pem.Type.EndsWith("CERTIFICATE"))
            {
                return new BcTlsCertificate(crypto as BcTlsCrypto, X509CertificateStructure.GetInstance(pem.Content));
            }
            throw new Exception("'resource' doesn't specify a valid certificate");
        }

        public static AsymmetricKeyParameter LoadPrivateKeyResource(X509Certificate2 certificate)
        {
            // TODO: When .NET Standard and Framework support are deprecated this pragma can be removed.
#pragma warning disable SYSLIB0028
            return DotNetUtilities.GetKeyPair(certificate.PrivateKey).Private;
#pragma warning restore SYSLIB0028
        }

        public static AsymmetricKeyParameter LoadPrivateKeyResource(string resource)
        {
            PemObject pem = LoadPemResource(resource);
            if (pem.Type.EndsWith("RSA PRIVATE KEY"))
            {
                RsaPrivateKeyStructure rsa = RsaPrivateKeyStructure.GetInstance(pem.Content);
                return new RsaPrivateCrtKeyParameters(rsa.Modulus,
                        rsa.PublicExponent, rsa.PrivateExponent,
                        rsa.Prime1, rsa.Prime2, rsa.Exponent1,
                        rsa.Exponent2, rsa.Coefficient);
            }
            if (pem.Type.EndsWith("PRIVATE KEY"))
            {
                return PrivateKeyFactory.CreateKey(pem.Content);
            }
            throw new Exception("'resource' doesn't specify a valid private key");
        }

        public static PemObject LoadPemResource(string path)
        {
            using (var s = new System.IO.StreamReader(path))
            {
                PemReader p = new PemReader(s);
                PemObject o = p.ReadPemObject();
                return o;
            }
            throw new Exception("'resource' doesn't specify a valid private key");
        }

        #region Self Signed Utils

        public static X509Certificate2 CreateSelfSignedCert(AsymmetricKeyParameter privateKey = null)
        {
            return CreateSelfSignedCert("CN=localhost", "CN=root", privateKey);
        }

        public static X509Certificate2 CreateSelfSignedCert(string subjectName, string issuerName, AsymmetricKeyParameter privateKey)
        {
            const int keyStrength = DEFAULT_KEY_SIZE;
            if (privateKey == null)
            {
                privateKey = CreatePrivateKeyResource(issuerName);
            }
            var issuerPrivKey = privateKey;

            // Generating Random Numbers
            var randomGenerator = new CryptoApiRandomGenerator();
            var random = new SecureRandom(randomGenerator);
            ISignatureFactory signatureFactory = new Asn1SignatureFactory("SHA256WITHRSA", issuerPrivKey, random);

            // The Certificate Generator
            var certificateGenerator = new X509V3CertificateGenerator();
            certificateGenerator.AddExtension(X509Extensions.SubjectAlternativeName, false, new GeneralNames(new GeneralName[] { new GeneralName(GeneralName.DnsName, "localhost"), new GeneralName(GeneralName.DnsName, "127.0.0.1") }));
            certificateGenerator.AddExtension(X509Extensions.ExtendedKeyUsage, true, new ExtendedKeyUsage(new List<DerObjectIdentifier>() { new DerObjectIdentifier("1.3.6.1.5.5.7.3.1") }));

            // Serial Number
            var serialNumber = BigIntegers.CreateRandomInRange(BigInteger.One, BigInteger.ValueOf(Int64.MaxValue), random);
            certificateGenerator.SetSerialNumber(serialNumber);

            // Issuer and Subject Name
            var subjectDn = new X509Name(subjectName);
            var issuerDn = new X509Name(issuerName);
            certificateGenerator.SetIssuerDN(issuerDn);
            certificateGenerator.SetSubjectDN(subjectDn);

            // Valid For
            var notBefore = DateTime.UtcNow.Date;
            var notAfter = notBefore.AddYears(70);

            certificateGenerator.SetNotBefore(notBefore);
            certificateGenerator.SetNotAfter(notAfter);

            // Subject Public Key
            var keyGenerationParameters = new KeyGenerationParameters(random, keyStrength);
            var keyPairGenerator = new RsaKeyPairGenerator();
            keyPairGenerator.Init(keyGenerationParameters);
            var subjectKeyPair = keyPairGenerator.GenerateKeyPair();

            certificateGenerator.SetPublicKey(subjectKeyPair.Public);

            // self sign certificate
            var certificate = certificateGenerator.Generate(signatureFactory);

            // Originally pre-processor defines were used to try and pick the supported way to get from a Bouncy Castle
            // certificate and private key to a .NET certificate. The problem is that setting the private key on a .NET
            // X509 certificate is possible in .NET Framework but NOT in .NET Core. To complicate matters even further
            // the workaround in the CovertBouncyCert method of saving a cert + pvt key to a .pfx stream and then
            // reloading does not work on macOS or Unity (and possibly elsewhere) due to .pfx serialisation not being
            // compatible. This is the exception from Unity:
            //
            // Mono.Security.ASN1..ctor (System.Byte[] data) (at <6a66fe237d4242c9924192d3c28dd540>:0)
            // Mono.Security.X509.X509Certificate.Parse(System.Byte[] data)(at < 6a66fe237d4242c9924192d3c28dd540 >:0)
            //
            // Summary:
            // .NET Framework (including Mono on Linux, macOS and WSL)
            //  - Set x509.PrivateKey works.
            // .NET Standard:
            //  - Set x509.PrivateKey for a .NET Framework application.
            //  - Set x509.PrivateKey for a .NET Core application FAILS.
            // .NET Core:
            //  - Set x509.PrivateKey for a .NET Core application FAILS.
            //  - PFX serialisation works on Windows.
            //  - PFX serialisation works on WSL and Linux.
            //  - PFX serialisation FAILS on macOS.
            //
            // For same issue see https://github.com/dotnet/runtime/issues/23635.
            // For fix in net5 see https://github.com/dotnet/corefx/pull/42226.
            try
            {
                // corresponding private key
                var info = Org.BouncyCastle.Pkcs.PrivateKeyInfoFactory.CreatePrivateKeyInfo(subjectKeyPair.Private);

                // merge into X509Certificate2
                var x509 = new X509Certificate2(certificate.GetEncoded());

                var seq = (Asn1Sequence)Asn1Object.FromByteArray(info.ParsePrivateKey().GetDerEncoded());
                if (seq.Count != 9)
                {
                    throw new Org.BouncyCastle.OpenSsl.PemException("malformed sequence in RSA private key");
                }

                var rsa = RsaPrivateKeyStructure.GetInstance(seq); //new RsaPrivateKeyStructure(seq);
                var rsaparams = new RsaPrivateCrtKeyParameters(
                    rsa.Modulus, rsa.PublicExponent, rsa.PrivateExponent, rsa.Prime1, rsa.Prime2, rsa.Exponent1, rsa.Exponent2, rsa.Coefficient);

                // TODO: When .NET Standard and Framework support are deprecated this pragma can be removed.
#pragma warning disable SYSLIB0028
                x509.PrivateKey = ToRSA(rsaparams);
#pragma warning restore SYSLIB0028
                return x509;
            }
            catch
            {
                return ConvertBouncyCert(certificate, subjectKeyPair);
            }
        }

        public static (Org.BouncyCastle.X509.X509Certificate certificate, AsymmetricKeyParameter privateKey) CreateSelfSignedBouncyCastleCert()
        {
            return CreateSelfSignedBouncyCastleCert("CN=localhost", "CN=root", null);
        }

        public static (Org.BouncyCastle.X509.X509Certificate certificate, AsymmetricKeyParameter privateKey) CreateSelfSignedBouncyCastleCert(string subjectName, string issuerName, AsymmetricKeyParameter issuerPrivateKey)
        {
            const int keyStrength = DEFAULT_KEY_SIZE;
            if (issuerPrivateKey == null)
            {
                issuerPrivateKey = CreatePrivateKeyResource(issuerName);
            }

            // Generating Random Numbers
            var randomGenerator = new CryptoApiRandomGenerator();
            var random = new SecureRandom(randomGenerator);
            ISignatureFactory signatureFactory = new Asn1SignatureFactory("SHA256WITHRSA", issuerPrivateKey, random);

            // The Certificate Generator
            var certificateGenerator = new X509V3CertificateGenerator();
            certificateGenerator.AddExtension(X509Extensions.SubjectAlternativeName, false, new GeneralNames(new GeneralName[] { new GeneralName(GeneralName.DnsName, "localhost"), new GeneralName(GeneralName.DnsName, "127.0.0.1") }));
            certificateGenerator.AddExtension(X509Extensions.ExtendedKeyUsage, true, new ExtendedKeyUsage(new List<DerObjectIdentifier>() { new DerObjectIdentifier("1.3.6.1.5.5.7.3.1") }));

            // Serial Number
            var serialNumber = BigIntegers.CreateRandomInRange(BigInteger.One, BigInteger.ValueOf(Int64.MaxValue), random);
            certificateGenerator.SetSerialNumber(serialNumber);

            // Issuer and Subject Name
            var subjectDn = new X509Name(subjectName);
            var issuerDn = new X509Name(issuerName);
            certificateGenerator.SetIssuerDN(issuerDn);
            certificateGenerator.SetSubjectDN(subjectDn);

            // Valid For
            var notBefore = DateTime.UtcNow.Date;
            var notAfter = notBefore.AddYears(70);

            certificateGenerator.SetNotBefore(notBefore);
            certificateGenerator.SetNotAfter(notAfter);

            // Subject Public Key
            var keyGenerationParameters = new KeyGenerationParameters(random, keyStrength);
            var keyPairGenerator = new RsaKeyPairGenerator();
            keyPairGenerator.Init(keyGenerationParameters);
            var subjectKeyPair = keyPairGenerator.GenerateKeyPair();

            certificateGenerator.SetPublicKey(subjectKeyPair.Public);

            // self sign certificate
            var certificate = certificateGenerator.Generate(signatureFactory);

            return (certificate, subjectKeyPair.Private);
        }

        public static (Org.BouncyCastle.Tls.Certificate certificate, AsymmetricKeyParameter privateKey) CreateSelfSignedTlsCert(TlsCrypto crypto)
        {
            return CreateSelfSignedTlsCert(crypto, "CN=localhost", "CN=root", null);
        }

        public static (Org.BouncyCastle.Tls.Certificate certificate, AsymmetricKeyParameter privateKey) CreateSelfSignedTlsCert(TlsCrypto crypto, string subjectName, string issuerName, AsymmetricKeyParameter issuerPrivateKey)
        {
            var tuple = CreateSelfSignedBouncyCastleCert(subjectName, issuerName, issuerPrivateKey);
            var certificate = tuple.certificate;
            var privateKey = tuple.privateKey;
            var chain = new TlsCertificate[] { new BcTlsCertificate(crypto as BcTlsCrypto, X509CertificateStructure.GetInstance(certificate.GetEncoded())) };
            var tlsCertificate = new Org.BouncyCastle.Tls.Certificate(chain);

            return (tlsCertificate, privateKey);
        }

        /// <remarks>Plagiarised from https://github.com/CryptLink/CertBuilder/blob/master/CertBuilder.cs.
        /// NOTE: netstandard2.1+ and netcoreapp3.1+ have x509.CopyWithPrivateKey which will avoid the need to
        /// use the serialize/deserialize from pfx to get from bouncy castle to .NET Core X509 certificates.</remarks>
        public static X509Certificate2 ConvertBouncyCert(Org.BouncyCastle.X509.X509Certificate bouncyCert, AsymmetricCipherKeyPair keyPair)
        {
#if !NET461 && !NETSTANDARD2_0
            var info = Org.BouncyCastle.Pkcs.PrivateKeyInfoFactory.CreatePrivateKeyInfo(keyPair.Private);

            //// merge into X509Certificate2
            var x509 = new X509Certificate2(bouncyCert.GetEncoded());

            var seq = (Asn1Sequence)Asn1Object.FromByteArray(info.ParsePrivateKey().GetDerEncoded());
            if (seq.Count != 9)
            {
                throw new Org.BouncyCastle.OpenSsl.PemException("malformed sequence in RSA private key");
            }

            var rsa = RsaPrivateKeyStructure.GetInstance(seq); //new RsaPrivateKeyStructure(seq);
            var rsaparams = new RsaPrivateCrtKeyParameters(
                rsa.Modulus, rsa.PublicExponent, rsa.PrivateExponent, rsa.Prime1, rsa.Prime2, rsa.Exponent1, rsa.Exponent2, rsa.Coefficient);

            return x509.CopyWithPrivateKey(ToRSA(rsaparams));

#else
            X509Certificate2 x509 = null;

            using (MemoryStream ms = new MemoryStream())
            {
                using (StreamWriter tw = new StreamWriter(ms))
                {
                    PemWriter pw = new PemWriter(tw);
                    //PemObject po = new PemObject("CERTIFICATE", bouncyCert.GetEncoded());
                    PemObject po = new PemObject("CERTIFICATE", bouncyCert.GetEncoded());
                    pw.WriteObject(po);

                    logger.LogDebug(System.Text.Encoding.UTF8.GetString(ms.GetBuffer()));

                    StreamWriter sw2 = new StreamWriter("test.cer");
                    sw2.Write(ms.GetBuffer());
                    sw2.Close();

                    x509 = new X509Certificate2(bouncyCert.GetEncoded());
                }
            }

            return x509;
#endif
        }


        public static AsymmetricKeyParameter CreatePrivateKeyResource(string subjectName = "CN=root")
        {
            const int keyStrength = DEFAULT_KEY_SIZE;

            // Generating Random Numbers
            var randomGenerator = new CryptoApiRandomGenerator();
            var random = new SecureRandom(randomGenerator);

            // The Certificate Generator
            //var certificateGenerator = new X509V3CertificateGenerator();

            //// Serial Number
            //var serialNumber = BigIntegers.CreateRandomInRange(BigInteger.One, BigInteger.ValueOf(long.MaxValue), random);
            //certificateGenerator.SetSerialNumber(serialNumber);

            //// Issuer and Subject Name
            //var subjectDn = new X509Name(subjectName);
            //var issuerDn = subjectDn;
            //certificateGenerator.SetIssuerDN(issuerDn);
            //certificateGenerator.SetSubjectDN(subjectDn);

            //// Valid For
            //var notBefore = DateTime.UtcNow.Date;
            //var notAfter = notBefore.AddYears(70);

            //certificateGenerator.SetNotBefore(notBefore);
            //certificateGenerator.SetNotAfter(notAfter);

            // Subject Public Key
            var keyGenerationParameters = new KeyGenerationParameters(random, keyStrength);
            var keyPairGenerator = new RsaKeyPairGenerator();
            keyPairGenerator.Init(keyGenerationParameters);
            var subjectKeyPair = keyPairGenerator.GenerateKeyPair();

            return subjectKeyPair.Private;
        }

        #endregion

        /// <summary>
        /// This method and the related ones have been copied from the BouncyCode DotNetUtilities 
        /// class due to https://github.com/bcgit/bc-csharp/issues/160 which prevents the original
        /// version from working on non-Windows platforms.
        /// </summary>
        public static RSA ToRSA(RsaPrivateCrtKeyParameters privKey)
        {
            return CreateRSAProvider(ToRSAParameters(privKey));
        }

        private static RSA CreateRSAProvider(RSAParameters rp)
        {
            //CspParameters csp = new CspParameters();
            //csp.KeyContainerName = string.Format("BouncyCastle-{0}", Guid.NewGuid());
            //RSACryptoServiceProvider rsaCsp = new RSACryptoServiceProvider(csp);
            RSACryptoServiceProvider rsaCsp = new RSACryptoServiceProvider();
            rsaCsp.ImportParameters(rp);
            return rsaCsp;
        }

        public static RSAParameters ToRSAParameters(RsaPrivateCrtKeyParameters privKey)
        {
            RSAParameters rp = new RSAParameters();
            rp.Modulus = privKey.Modulus.ToByteArrayUnsigned();
            rp.Exponent = privKey.PublicExponent.ToByteArrayUnsigned();
            rp.P = privKey.P.ToByteArrayUnsigned();
            rp.Q = privKey.Q.ToByteArrayUnsigned();
            rp.D = ConvertRSAParametersField(privKey.Exponent, rp.Modulus.Length);
            rp.DP = ConvertRSAParametersField(privKey.DP, rp.P.Length);
            rp.DQ = ConvertRSAParametersField(privKey.DQ, rp.Q.Length);
            rp.InverseQ = ConvertRSAParametersField(privKey.QInv, rp.Q.Length);
            return rp;
        }

        private static byte[] ConvertRSAParametersField(BigInteger n, int size)
        {
            byte[] bs = n.ToByteArrayUnsigned();

            if (bs.Length == size)
            {
                return bs;
            }

            if (bs.Length > size)
            {
                throw new ArgumentException("Specified size too small", "size");
            }

            byte[] padded = new byte[size];
            Array.Copy(bs, 0, padded, size - bs.Length, bs.Length);
            return padded;
        }

        /// <summary>
        /// Verifies the hash algorithm is supported by the utility functions in this class.
        /// </summary>
        /// <param name="hashAlgorithm">The hash algorithm to check.</param>
        public static bool IsHashSupported(string hashAlgorithm)
        {
            switch (hashAlgorithm.ToLower())
            {
                case "sha1":
                case "sha-1":
                case "sha256":
                case "sha-256":
                case "sha384":
                case "sha-384":
                case "sha512":
                case "sha-512":
                    return true;
                default:
                    return false;
            }
        }
    }
}
