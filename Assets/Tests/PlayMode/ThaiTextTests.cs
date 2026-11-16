using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace ScamSim.Tests
{
    /// <summary>
    /// Renders Thai text with stacked vowels and tone marks so a person (or Claude) can look at
    /// the PNG and confirm the marks sit in the right place. It only fails when rendering breaks.
    /// </summary>
    public class ThaiTextTests
    {
        const string Sample =
            "ที่นี่ น้ำ กุ้ง ปั๊ม ฝั่ง ญี่ปุ่น ปู่ ฤๅษี ฯลฯ ๑๒๓\n" +
            "สวัสดีครับ ผมร้อยตำรวจเอกธนากร จากกองปราบไซเบอร์กลาง\n" +
            "ตรวจพบว่าบัญชีธนาคารในชื่อของคุณพัวพันกับคดีฟอกเงิน";

        GameObject _host;

        [TearDown]
        public void TearDown()
        {
            if (_host != null) Object.Destroy(_host);
        }

#if UNITY_EDITOR
        [UnityTest]
        public IEnumerator ThaiMarksRender()
        {
            var settings = AssetDatabase.LoadAssetAtPath<PanelSettings>("Assets/UI/ScamSimPanelSettings.asset");
            var styles = AssetDatabase.LoadAssetAtPath<StyleSheet>("Assets/UI/ScamSim.uss");
            Assert.IsNotNull(settings, "run ProjectSetup.EnsurePanelSettings first");
            Assert.IsNotNull(styles);

            _host = new GameObject("ThaiTextTest");
            var document = _host.AddComponent<UIDocument>();
            document.panelSettings = settings;
            RenderTexture texture = UiCapture.Redirect(document);
            yield return null;

            VisualElement root = document.rootVisualElement;
            root.styleSheets.Add(styles);
            root.AddToClassList("scamsim-root");
            root.style.paddingLeft = 40;
            root.style.paddingTop = 40;

            foreach (int size in new[] { 18, 24, 40 })
            {
                var label = new Label(Sample);
                label.style.fontSize = size;
                label.style.marginBottom = 24;
                root.Add(label);
            }
            var bold = new Label("ตัวหนา: ห้ามโอนเงินไป \"บัญชีปลอดภัย\" เด็ดขาด");
            bold.AddToClassList("bold");
            bold.style.fontSize = 40;
            root.Add(bold);

            string saved = null;
            yield return UiCapture.Save(texture, "thai_text", p => saved = p);
            Assert.IsNotNull(saved);
        }
#endif
    }
}
