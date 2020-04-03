using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using WebRTC;

public class StartWebRtcStreamOnEnable : MonoBehaviour
{
    [SerializeField]
    protected string serverName = null;

    [SerializeField]
    protected bool reusePrefab = false;

    [SerializeField]
    protected GameObject prefab = null;

    [SerializeField]
    protected Transform parent = null;

    protected GameObject gameObj;


    protected virtual GameObject GetPrefab()
    {
        return prefab;
    }

    protected void OnEnable()
    {
        StartCoroutine(TryConnect());
    }

    protected void OnDisable()
    {
        if (!reusePrefab && gameObj != null)
        {
            Destroy(gameObj);
            gameObj = null;
        }
    }

    protected IEnumerator TryConnect()
    {
        while (WebRtcServer.Instance == null)
        {
            yield return null;
        }

        if (gameObj == null)
        {
            gameObj = Instantiate(GetPrefab(), parent);
        }

        OnPrefabCreated();

        WebRtcServer.Instance.ConsumeWebRtcStream(serverName, gameObj);
    }

    protected virtual void OnPrefabCreated()
    {

    }
}
