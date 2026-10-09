using UnityEngine;
using UnityEngine.SceneManagement;

namespace AssetStoreTools.Validator.Services.Validation
{
    internal interface ISceneUtilityService : IValidatorService
    {
        string CurrentScenePath { get; }

        System.IDisposable BeginScan();
        Scene OpenScene(string scenePath);
        GameObject[] GetRootGameObjects();
    }
}