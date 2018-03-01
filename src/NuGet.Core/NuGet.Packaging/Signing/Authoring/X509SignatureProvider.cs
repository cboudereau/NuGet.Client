// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Common;

#if IS_DESKTOP
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
#endif

namespace NuGet.Packaging.Signing
{
    /// <summary>
    /// Sign a manifest hash with an X509Certificate2.
    /// </summary>
    public class X509SignatureProvider : ISignatureProvider
    {
        // Occurs when SignedCms.ComputeSignature cannot read the certificate private key
        // "Invalid provider type specified." (INVALID_PROVIDER_TYPE)
        private const int INVALID_PROVIDER_TYPE_HRESULT = unchecked((int)0x80090014);

        private readonly ITimestampProvider _timestampProvider;

        public X509SignatureProvider(ITimestampProvider timestampProvider)
        {
            _timestampProvider = timestampProvider;
        }

        /// <summary>
        /// Sign the package stream hash with a X509Certificate2.
        /// </summary>
        public Task<PrimarySignature> CreatePrimarySignatureAsync(SignPackageRequest request, SignatureContent signatureContent, ILogger logger, CancellationToken token)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (signatureContent == null)
            {
                throw new ArgumentNullException(nameof(signatureContent));
            }

            if (logger == null)
            {
                throw new ArgumentNullException(nameof(logger));
            }

            var signature = CreatePrimarySignature(request, signatureContent, logger);

            if (_timestampProvider == null)
            {
                return Task.FromResult(signature);
            }
            else
            {
                return TimestampPrimarySignatureAsync(request, logger, signature, token);
            }
        }

        /// <summary>
        /// Countersign the primary signature with a X509Certificate2.
        /// </summary>
        public Task<PrimarySignature> CreateRepositoryCountersignatureAsync(RepositorySignPackageRequest request, PrimarySignature primarySignature, ILogger logger, CancellationToken token)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (primarySignature == null)
            {
                throw new ArgumentNullException(nameof(primarySignature));
            }

            if (logger == null)
            {
                throw new ArgumentNullException(nameof(logger));
            }

            var signature = CreateRepositoryCountersignature(request, primarySignature, logger);

            if (_timestampProvider == null)
            {
                return Task.FromResult(signature);
            }
            else
            {
                return TimestampRepositoryCountersignatureAsync(request, logger, signature, token);
            }
        }

#if IS_DESKTOP
        private PrimarySignature CreatePrimarySignature(SignPackageRequest request, SignatureContent signatureContent, ILogger logger)
        {
            var cmsSigner = SigningUtility.CreateCmsSigner(request, logger);

            if (request.PrivateKey != null)
            {
                return CreatePrimarySignature(cmsSigner, signatureContent.GetBytes(), request.PrivateKey);
            }

            return CreatePrimarySignature(cmsSigner, request, signatureContent.GetBytes());
        }

        private PrimarySignature CreateRepositoryCountersignature(SignPackageRequest request, PrimarySignature primarySignature, ILogger logger)
        {
            var cmsSigner = CreateCmsSigner(request, logger);

            if (request.PrivateKey != null)
            {
                return CreateRepositoryCountersignature(cmsSigner, primarySignature, request.PrivateKey);
            }

            return CreateRepositoryCountersignature(cmsSigner, request, primarySignature);
        }

        private static CmsSigner CreateCmsSigner(SignPackageRequest request, ILogger logger)
        {
            // Subject Key Identifier (SKI) is smaller and less prone to accidental matching than issuer and serial
            // number.  However, to ensure cross-platform verification, SKI should only be used if the certificate
            // has the SKI extension attribute.
            CmsSigner signer;

            if (request.Certificate.Extensions[Oids.SubjectKeyIdentifier] == null)
            {
                signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, request.Certificate);
            }
            else
            {
                signer = new CmsSigner(SubjectIdentifierType.SubjectKeyIdentifier, request.Certificate);
            }

            request.BuildSigningCertificateChainOnce(logger);

            var chain = request.Chain;

            foreach (var certificate in chain)
            {
                signer.Certificates.Add(certificate);
            }

            var attributes = SigningUtility.CreateSignedAttributes(request, chain);

            foreach (var attribute in attributes)
            {
                signer.SignedAttributes.Add(attribute);
            }

            // We built the chain ourselves and added certificates.
            // Passing any other value here would trigger another chain build
            // and possibly add duplicate certs to the collection.
            signer.IncludeOption = X509IncludeOption.None;
            signer.DigestAlgorithm = request.SignatureHashAlgorithm.ConvertToOid();

            return signer;
        }

        private PrimarySignature CreatePrimarySignature(CmsSigner cmsSigner, byte[] signingData, CngKey privateKey)
        {
            var cms = NativeUtilities.NativeSign(cmsSigner, signingData, privateKey);

            return PrimarySignature.Load(cms);
        }

        private PrimarySignature CreatePrimarySignature(CmsSigner cmsSigner, SignPackageRequest request, byte[] signingData)
        {
            var contentInfo = new ContentInfo(signingData);
            var cms = new SignedCms(contentInfo);

            try
            {
                cms.ComputeSignature(cmsSigner);
            }
            catch (CryptographicException ex) when (ex.HResult == INVALID_PROVIDER_TYPE_HRESULT)
            {
                var exceptionBuilder = new StringBuilder();
                exceptionBuilder.AppendLine(Strings.SignFailureCertificateInvalidProviderType);
                exceptionBuilder.AppendLine(CertificateUtility.X509Certificate2ToString(request.Certificate, Common.HashAlgorithmName.SHA256));

                throw new SignatureException(NuGetLogCode.NU3001, exceptionBuilder.ToString());
            }

            return PrimarySignature.Load(cms);
        }

        private PrimarySignature CreateRepositoryCountersignature(CmsSigner cmsSigner, PrimarySignature primarySignature, CngKey privateKey)
        {
            using (var primarySignatureNativeCms = NativeCms.Decode(primarySignature.GetBytes()))
            {
                primarySignatureNativeCms.AddCounterSignature(cmsSigner, privateKey);

                var bytes = primarySignatureNativeCms.Encode();
                var updatedCms = new SignedCms();

                updatedCms.Decode(bytes);

                return PrimarySignature.Load(updatedCms);
            }
        }

        private PrimarySignature CreateRepositoryCountersignature(CmsSigner cmsSigner, SignPackageRequest request, PrimarySignature primarySignature)
        {
            try
            {
                primarySignature.SignerInfo.ComputeCounterSignature(cmsSigner);
            }
            catch (CryptographicException ex) when (ex.HResult == INVALID_PROVIDER_TYPE_HRESULT)
            {
                var exceptionBuilder = new StringBuilder();
                exceptionBuilder.AppendLine(Strings.SignFailureCertificateInvalidProviderType);
                exceptionBuilder.AppendLine(CertificateUtility.X509Certificate2ToString(request.Certificate, Common.HashAlgorithmName.SHA256));

                throw new SignatureException(NuGetLogCode.NU3001, exceptionBuilder.ToString());
            }

            return primarySignature;
        }

        private Task<PrimarySignature> TimestampPrimarySignatureAsync(SignPackageRequest request, ILogger logger, PrimarySignature signature, CancellationToken token)
        {
            var signatureValue = signature.GetSignatureValue();
            var messageHash = request.TimestampHashAlgorithm.ComputeHash(signatureValue);

            var timestampRequest = new TimestampRequest(
                signingSpec: SigningSpecifications.V1,
                signatureMessageHash: messageHash,
                timestampHashAlgorithm: request.TimestampHashAlgorithm,
                timestampSignaturePlacement: SignaturePlacement.PrimarySignature
            );

            return _timestampProvider.TimestampSignatureAsync(signature, timestampRequest, logger, token);
        }

        private Task<PrimarySignature> TimestampRepositoryCountersignatureAsync(SignPackageRequest request, ILogger logger, PrimarySignature primarySignature, CancellationToken token)
        {
            var repositoryCountersignature = RepositoryCountersignature.GetRepositoryCountersignature(primarySignature);
            var signatureValue = repositoryCountersignature.GetSignatureValue();
            var messageHash = request.TimestampHashAlgorithm.ComputeHash(signatureValue);

            var timestampRequest = new TimestampRequest(
                signingSpec: SigningSpecifications.V1,
                signatureMessageHash: messageHash,
                timestampHashAlgorithm: request.TimestampHashAlgorithm,
                timestampSignaturePlacement: SignaturePlacement.Countersignature
            );

            return _timestampProvider.TimestampSignatureAsync(primarySignature, timestampRequest, logger, token);
        }

#else
        private PrimarySignature CreatePrimarySignature(SignPackageRequest request, SignatureContent signatureContent, ILogger logger)
        {
            throw new NotSupportedException();
        }

        private Task<PrimarySignature> TimestampPrimarySignatureAsync(SignPackageRequest request, ILogger logger, PrimarySignature signature, CancellationToken token)
        {
            throw new NotSupportedException();
        }

        private PrimarySignature CreateRepositoryCountersignature(SignPackageRequest request, PrimarySignature signature, ILogger logger)
        {
            throw new NotSupportedException();
        }

        private Task<PrimarySignature> TimestampRepositoryCountersignatureAsync(SignPackageRequest request, ILogger logger, PrimarySignature signature, CancellationToken token)
        {
            throw new NotSupportedException();
        }
#endif
    }
}