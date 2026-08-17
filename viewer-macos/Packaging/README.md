# Packaging

Dateien für die App-Bundle-Variante des Viewers.

## Warum das hier liegt und nicht bei den Quellen

SwiftPM lehnt ein `Info.plist` als Top-Level-Ressource ausdrücklich ab — es würde
mit dem Info.plist des erzeugten Bundles kollidieren. `swift build` erzeugt
ohnehin nur eine ausführbare Datei, kein `.app`.

Für ein verteilbares `AppControl Viewer.app` mit Icon, Dock-Eintrag und dem
Erklärungstext für die Bedienungshilfen-Berechtigung braucht es ein
Xcode-App-Projekt, das dieses SwiftPM-Paket als lokale Abhängigkeit einbindet:

1. Xcode → File → New → Project → macOS → App
2. Im neuen Projekt: File → Add Package Dependencies → Add Local → `viewer-macos`
3. Unter „Frameworks, Libraries, and Embedded Content" `AppControlViewer` hinzufügen
4. Den Inhalt von [`Info.plist`](Info.plist) in das Info.plist des App-Targets übernehmen —
   insbesondere `NSAccessibilityUsageDescription`

Zum Ausprobieren reicht `swift build -c release` und der Start aus dem Terminal;
die Bedienungshilfen-Berechtigung lässt sich dann allerdings nicht sauber
anfordern, weil macOS dafür ein signiertes Bundle erwartet.

## `NSAccessibilityUsageDescription`

Der Text ist bewusst ausführlich. Die Bedienungshilfen-Berechtigung erlaubt es
einer App, systemweit Tastatureingaben mitzulesen — das ist mächtig genug, dass
eine vage Formulierung („AppControl benötigt diese Berechtigung") unangemessen
wäre. Der Text nennt konkret, wofür sie verwendet wird und dass die App ohne sie
vollständig funktioniert.
