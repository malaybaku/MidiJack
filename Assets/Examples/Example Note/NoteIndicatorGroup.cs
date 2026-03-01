using UnityEngine;
using MidiJack;

public class NoteIndicatorGroup : MonoBehaviour
{
    public GameObject prefab;

    void Start()
    {
        WindowsMidiInterop.Instance.SetActive(true);
        for (var i = 0; i < 128; i++)
        {
            var go = Instantiate<GameObject>(prefab);
            go.transform.position = new Vector3(i % 12, i / 12, 0);
            go.GetComponent<NoteIndicator>().noteNumber = i;
        }
    }
}
