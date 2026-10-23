namespace ScormEditor.Services;

/// <summary>A small SCORM 1.2 course used to try the editor without uploading anything.</summary>
public static class SamplePackage
{
    public const string FileName = "fire-safety-basics.zip";

    public static ScormPackage Create() => new(FileName, Files.Select(f => new KeyValuePair<string, byte[]>(f.Key, System.Text.Encoding.UTF8.GetBytes(f.Value))));

    private static readonly Dictionary<string, string> Files = new()
    {
        ["imsmanifest.xml"] = """
<?xml version="1.0" encoding="UTF-8"?>
<manifest identifier="com.example.fire-safety" version="1.0"
  xmlns="http://www.imsproject.org/xsd/imscp_rootv1p1p2"
  xmlns:adlcp="http://www.adlnet.org/xsd/adlcp_rootv1p2"
  xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
  xsi:schemaLocation="http://www.imsproject.org/xsd/imscp_rootv1p1p2 imscp_rootv1p1p2.xsd http://www.adlnet.org/xsd/adlcp_rootv1p2 adlcp_rootv1p2.xsd">
  <metadata>
    <schema>ADL SCORM</schema>
    <schemaversion>1.2</schemaversion>
  </metadata>
  <organizations default="ORG-1">
    <organization identifier="ORG-1">
      <title>Fire Safety Basics</title>
      <item identifier="ITEM-1" identifierref="RES-1">
        <title>Module 1: Know your exits</title>
        <adlcp:masteryscore>80</adlcp:masteryscore>
      </item>
    </organization>
  </organizations>
  <resources>
    <resource identifier="RES-1" type="webcontent" adlcp:scormtype="sco" href="index.html">
      <file href="index.html"/>
      <file href="quiz.html"/>
      <file href="css/course.css"/>
      <file href="js/scorm.js"/>
      <file href="img/exit-sign.svg"/>
    </resource>
  </resources>
</manifest>
""",
        ["index.html"] = """
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Know your exits</title>
  <link rel="stylesheet" href="css/course.css">
  <script src="js/scorm.js"></script>
</head>
<body>
  <main class="slide">
    <p class="eyebrow">Module 1 · Fire Safety Basics</p>
    <h1>Know your exits</h1>
    <img src="img/exit-sign.svg" alt="Green emergency exit sign" width="220">
    <p>Before an emergency, find the two exits closest to your desk. Count the doors between you and each exit, because smoke can make signs hard to see.</p>
    <ul>
      <li>Never use the lifts during a fire alarm.</li>
      <li>Close doors behind you to slow the spread of smoke.</li>
      <li>Go straight to the assembly point and wait for the fire warden.</li>
    </ul>
    <a class="next" href="quiz.html">Take the check →</a>
  </main>
  <script>Course.start("intro");</script>
</body>
</html>
""",
        ["quiz.html"] = """
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Knowledge check</title>
  <link rel="stylesheet" href="css/course.css">
  <script src="js/scorm.js"></script>
</head>
<body>
  <main class="slide">
    <p class="eyebrow">Knowledge check</p>
    <h1>The fire alarm sounds. What do you do first?</h1>
    <form id="q">
      <label><input type="radio" name="a" value="0"> Take the lift to the ground floor</label>
      <label><input type="radio" name="a" value="1"> Leave by the nearest exit, closing doors behind you</label>
      <label><input type="radio" name="a" value="0"> Finish your email, then leave</label>
      <button type="submit">Submit answer</button>
    </form>
    <p id="result" class="result" hidden></p>
    <a class="back" href="index.html">← Back to the lesson</a>
  </main>
  <script>
    Course.start("quiz");
    document.getElementById("q").addEventListener("submit", function (e) {
      e.preventDefault();
      var pick = document.querySelector("input[name=a]:checked");
      var right = pick && pick.value === "1";
      var out = document.getElementById("result");
      out.hidden = false;
      out.textContent = right ? "Correct. Leave calmly by the nearest exit." : "Not quite. Lifts can fail in a fire, so use the stairs.";
      Course.finish(right ? 100 : 0);
    });
  </script>
</body>
</html>
""",
        ["css/course.css"] = """
body {
  margin: 0;
  font-family: Georgia, "Times New Roman", serif;
  background: #f6f4ef;
  color: #222;
}
.slide {
  max-width: 640px;
  margin: 0 auto;
  padding: 40px 24px;
}
.eyebrow {
  font: 600 12px/1 system-ui, sans-serif;
  letter-spacing: .08em;
  text-transform: uppercase;
  color: #b3261e;
}
h1 { font-size: 30px; line-height: 1.15; margin: 8px 0 20px; }
img { display: block; max-width: 100%; margin: 0 0 20px; }
li { margin: 6px 0; }
form label { display: block; padding: 10px 12px; margin: 8px 0; background: #fff; border: 1px solid #ddd; border-radius: 6px; font-family: system-ui, sans-serif; }
button, .next, .back {
  display: inline-block; margin-top: 16px; padding: 10px 16px; border-radius: 6px;
  background: #1d7a3a; color: #fff; text-decoration: none; border: 0; font: 600 14px system-ui, sans-serif; cursor: pointer;
}
.back { background: #555; }
.result { font-family: system-ui, sans-serif; font-weight: 600; }
""",
        ["js/scorm.js"] = """
// Minimal SCORM 1.2 wrapper used by every page of the course.
var Course = (function () {
  function findAPI(win) {
    for (var i = 0; win && i < 10; i++) {
      if (win.API) return win.API;
      if (win === win.parent) break;
      win = win.parent;
    }
    return null;
  }
  var api = findAPI(window);
  return {
    start: function (location) {
      if (!api) return;
      api.LMSInitialize("");
      if (api.LMSGetValue("cmi.core.lesson_status") === "not attempted") {
        api.LMSSetValue("cmi.core.lesson_status", "incomplete");
      }
      api.LMSSetValue("cmi.core.lesson_location", location);
      api.LMSCommit("");
    },
    finish: function (score) {
      if (!api) return;
      api.LMSSetValue("cmi.core.score.raw", String(score));
      api.LMSSetValue("cmi.core.lesson_status", score >= 80 ? "passed" : "failed");
      api.LMSCommit("");
    }
  };
})();
""",
        ["img/exit-sign.svg"] = """
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 220 110">
  <rect width="220" height="110" rx="8" fill="#1d7a3a"/>
  <rect x="10" y="10" width="200" height="90" rx="4" fill="none" stroke="#fff" stroke-width="3"/>
  <circle cx="58" cy="32" r="9" fill="#fff"/>
  <path d="M55 44l-14 18 10 4 6-9 4 14-12 18 9 5 12-18 10 10 8-6-14-16-4-14 12 6 8-10-10-5z" fill="#fff"/>
  <path d="M120 55h56m-14-14 14 14-14 14" stroke="#fff" stroke-width="9" fill="none" stroke-linecap="round" stroke-linejoin="round"/>
</svg>
""",
    };
}
