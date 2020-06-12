using System.Collections;
using System.Collections.Generic;
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
                    (new ZuKey("key", ("index", 0), ("temp", 1)), 1),
                    (new ZuKey("key", ("index", 1), ("temp", 2)), 2)
                );
            }
        );
    }

    // Update is called once per frame
    void Update()
    {

    }
}
