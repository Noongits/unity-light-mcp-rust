using AssetStoreTools.Api;
using AssetStoreTools.Api.Responses;
using AssetStoreTools.Uploader.Services.Analytics.Data;
using System;
using System.Threading.Tasks;
using UnityEngine.Analytics;

namespace AssetStoreTools.Uploader.Data
{
    internal interface IWorkflowServices
    {
        Task<PackageUploadedUnityVersionDataResponse> GetPackageUploadedVersions(IPackage package, int timeoutMs);
        int UploadGeneration { get; }
        Task<PackageUploadResponse> UploadPackage(IPackageUploader uploader, IProgress<float> progress, int generation);
        void StopUploading(IPackageUploader uploader);
        AnalyticsResult SendAnalytic(IAssetStoreAnalytic data);
        Task<RefreshedPackageDataResponse> UpdatePackageData(IPackage package);
    }
}