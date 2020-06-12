using UnityEngine;
using ZuKitCore;

public class ZuKitSample : MonoBehaviour
{
    // Start is called before the first frame update
    void Start()
    {
        ZuKit.Setup(
            "http://localhost:8086",
            "myZuDB",
            () =>
            {
                return new ZuValues(
                    (new ZuKey("key", ("index", 0), ("temp", 1)), Time.frameCount),
                    (new ZuKey("key", ("index", 1), ("temp", 2)), Random.Range(-100 * Time.frameCount, 100 * Time.frameCount))
                );
            }
        );
    }

    // Update is called once per frame
    void Update()
    {

    }
}
