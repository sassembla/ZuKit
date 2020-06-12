# ZuKit
 
send unity editor data to InfluxDB.

![https://raw.githubusercontent.com/sassembla/ZuKit/master/docs/scr.png](https://raw.githubusercontent.com/sassembla/ZuKit/master/docs/scr.png)


## usage

```csharp
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
```


## license
see [here](https://github.com/sassembla/ZuKit/blob/master/LICENSE)