using AssetStoreTools.Api;
using AssetStoreTools.Api.Responses;
using System;
using System.Threading.Tasks;

namespace AssetStoreTools.Uploader.Services.Api
{
    internal interface IPackageUploadingService : IUploaderService
    {
        bool IsUploading { get; }

        int UploadGeneration { get; }
        Task<PackageUploadResponse> UploadPackage(IPackageUploader uploader, IProgress<float> progress, int generation);
        void StopUploading(IPackageUploader package);
        void StopAllUploadinng();
    }
}