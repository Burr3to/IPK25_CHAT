  # `IPK25-CHAT`
  
 ## Dokumentácia k 2. úlohe do IPK 2024/2025
 
**Autor:** Jakub Fiľo - xfiloja00

---
##  Teória k Projektu
 * TCP (Transmission Control Protocol)
Je to protokol transportnej vrstvy, ktorý umožnuje spolahlive pripojenie k cielovím zariadeniam. Ako prvé nadviaže spojenie pomocou **three-way handshake** kde sa overí pripojenie a až potom posiela dáta. Dáta mozu byt rozdelene do segmentov kvôli velkosti posielanych dat. Protokol TCP vykonova kontrolu aby zarucil ze sa packety nestratia, ku kazdemu packetu pridarí poradové cislo, ktore hovori prijemcovi ake packety mu prichadzaju alebo sa stratili, ak sa packet strati, to znamena ze odosielatel nedostane **ACK** (**acknowledgement**)- správu tak packet pošle znova. Prijemca tiez podla poradovych čisiel vie zostavit data podla poradia.
 * UDP (User Datagram Protocol)
Protokol transportnej vrstvy, označovany ako nespolahlivý, nemá zaruku dorucenia packetov. Pouzivatelia musia rátať s možnými stratami dát pri prenose, nespravnim poradim prichadzajucich packetov alebo prijem moznych duplikátov.
![Alternatívny text obrázka](img/TcpVsUdp.jpg)
### `IPK25-CHAT` z pohľadu OSI modelu
Funkcionality programu IPK25-CHAT sa dá rozdeliť na časti podľa sietoveho modelu OSI.
 * **L7 - Aplikačná vrstva**
	 * Priamo komunikuje s koncovým chatovacim klientom. V našom projekte táto čast zodpovedá za pravidlá komuníkacie s preddefinovaním typom správ ako sú napr. ***`AUTH, JOIN, MSG, REPLY, BYE, ERR, CONFIRM, PING.`*** Kde
 **`AUTH`** zodpovedá definicii spravy autentifikácie klienta po pripojení, kde používateľ zadá Username, DisplayName a Secret.
**`JOIN`** správa pomáha definovat klientovu ziadosť o pripojenie do kanálu na komunikovanie, obsahuje informacie o prezývke a identifikacnom čislo kanálu.
	* Na tejto vrstve sa tiež nachádza stavový automat, ktorý určuje programu postupnosť krokov, v prípade odoslaní správy a čakanie na odpoveď, tento priklád vystihuje začiatok komuniácie */auth* kde program pošle **AUTH** správu na a nasledovne čaká na odpoveď typu **REPLY**, ktorá môže byt pozitivna alebo negatívna.
*  **L6 - Prezentačná vrsta**
	* Zabezpecuje aby data boli v preddefinovanom formate, ktoré aplikačná vrstva dokaze prijat a nasledne poslat do siete. Každá položka má vlastne pravidlá v akom formáte sa moze vyskytovat. Ako priklad je správa typu **`AUTH`**, ktorá sa skládá z Username , DisplayName a Secret, finálna AUTH sprava ma formát ***AUTH {Username} AS {DisplayName} USING {Secret}\r\n)*** kde
		* Username - môže obsahovat iba malé a velke pismena, čisla, podčiarkovník a pomlčku s maximalnou dlzkou retazca 20 znakov.
		* DisplayName - može obsahovať všetky štandartne tlacitelne ASCII znaky  s maximalnou dlzkou retazca 20 znakov.
		* Secret - má pravidlá ako Username ale dlžka retazca je maximalne 128 znakov.
	* Pre UDP verziu projektu sa táto vrstva stará o binarnu strukturu správ, ktoré sa posielajú a upravuje poradie bajtov na sposob **Big-endian**.
* **L5 - Relačná vrstva**
	* Na tejto vrstve nam program nadvazuje, spravuje a ukončuje spojenie medzi dvoma zariadeniami.
	Pri **TCP** spojení sa vytvorí session pomocou *connect* kde prebehne **three-way handshake** a uzivatel moze zadavať príkazy.
	Pri **UDP** v projekte sa session identifikuje dynamickym portom servera, z ktoreho prichadzaju odpovede po prvej AUTH správe (prvá REPLY sprava). Uzivatel si musi tento port zapamätat aby vsetky dalsie spravy posielal tam.
* **L4 - Transportná vrstva**
	* Zodpoveda za prenos dat medzi koncovymi aplikaciami na roznych zariadeniach. V pripade TCP zabezpecuje spolahlive dorucenie dat a ich poradie. V projekte používame Socket objekt, ktory povie operačnemu systemu ako transportny protokol ideme pouziť, napr. `ProtocolType.Tcp` alebo `ProtocolType.Udp`.
	* Rozdieli pri TCP a UDP v našom projekte.
		* Pri TCP sa o pripojenie a udržiavanie pripojenie stará systém, tiez ako poradie a riadenie toku dát. Program sa stará iba o posielanie a prijimanie správ od klienta/servera.
		* Pri **`UDP`** nám systém nepomaha, každy datagram, ktory program vytvori sa moze stratit, duplikovat alebo prijst mimo poradia. V nasej implementacii mame UDP protokol upravený aby nas program takýmto problémom vedel predíst. Ako riešenia použiva správu **`CONFIRM`** ako odpoveď na rôzne správy ako napr.  **`JOIN`**, **`REPLY`**, **`BYE`**.

### Bibliografia

Cisco Networking Academy. IPv4 vs IPv6. Online. Available from: https://www.networkacademy.io/ccna/ipv6/ipv4-vs-ipv6 [Accessed 27 March 2025].

inc0x0. TCP/IP Packets Introduction - Part 3: Manually Create and Send Raw TCP/IP Packets. Online. Available from: https://inc0x0.com/tcp-ip-packets-introduction/tcp-ip-packets-3-manually-create-and-send-raw-tcp-ip-packets/ [Accessed 27 March 2025].

Wikipedia contributors. Transmission Control Protocol. Online. Available from: https://cs.wikipedia.org/wiki/Transmission_Control_Protocol [Accessed 27 March 2025].

Wikipedia contributors. User Datagram Protocol. Online. Available from: https://cs.wikipedia.org/wiki/User_Datagram_Protocol [Accessed 27 March 2025].

Microsoft. .NET API Browser. Online. Available from: https://learn.microsoft.com/en-us/dotnet/api/system.net?view=net-9.0 [Accessed 27 March 2025].

Nmap Project. scanme.nmap.org. Online. Available from: http://scanme.nmap.org/ [Accessed 27 March 2025].
