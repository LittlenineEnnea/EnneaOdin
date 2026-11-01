Heimdall (c) 2010-2017 Benjamin Dobell, Glass Echidna
https://www.glassechidna.com.au/products/heimdall/

DISCLAIMER:

    This software attempts to flash your Galaxy S device. The very nature of
    flashing is dangerous. As with all flashing software, Heimdall has the
    potential to damage (brick) your device if not used carefully. If you're
    concerned, don't use this software. Flashing ROMs onto your device may also
    void your warranty. Benjamin Dobell and Glass Echidna are not responsible
    for the result of your actions.


# Flashing Heimdall Firmware Package with Heimdall Frontend

    As of Heimdall Frontend 1.3 there are now two main ways to flash a ROM from
    Heimdall Frontend. The simpler and preferred option is to download a
    Heimdall Firmware Package and follow the following steps.


    1. Fully charge your device (use the wall charger as it's faster).

    2. Open the Heimdall Frontend (heimdall-frontend) application.

    3. From the "Load Package" tab, under the "Heimdall Firmware Package"
       section click the "Browse" button.

    4. Use the dialogue that appears to navigate to, and select, the Heimdall
       firmware package that you wish to flash.

    5. You will see progress bars appear as the package is decompressed and
       extracted.

       When the package has finished being decompressed you should see
       information about the particular firmware package that has been
       selected.

    6. Verify that your device is listed under "Supported Devices". If it's not
       then STOP immediately! DO NOT flash this firmware to your device!
       Instead search for an appropriate firmware package for your device.

       If you believe there is a mistake and your device is actually
       supported please get in contact with the firmware developer (not Glass
       Echidna!) and ask them to rectify the issue. If the developer provided
       a URL you may be able to contact them by pressing the "Homepage" button.

    7. If you've verified your device is supported you may continue to press
       the "Load / Customise" button.

    8. You should now be looking at the "Flash" tab. If not verify that you did
       in fact push the "Load / Customise" button.

       Generally, you won't NEED or WANT to customise a firmware package! In
       which case you can safely move on to step 9.

       Nonetheless, the "Flash" tab provides you with a means to customise the
       firmware package before flashing it to your device. See "Performing a
       Custom Flash with Heimdall Frontend" for more details.

    9. Put your Galaxy S device into download mode and plug it in to your PC.

       Download mode can be accessed several different ways depending on your
       particular device model. If you're unsure how to do this please search
       online for the appropriate method.

   10. Press the "Start" button.

   11. Heimdall Frontend will display the progress and inform you when the
       flash is complete.

       If something went wrong i.e. your device wasn't detected because it
       wasn't in download mode, then the status section will let you know the
       cause of the problem.

# How to Create a Heimdall Firmware Package

    Firstly, Heimdall's firmware package format is just a regular TAR archive
    compressed with gzip. The only two real requirements are that a valid
    firmware.xml must be included (refer to Appendix A) and you can only
    include files (no directories, links etc.) As such if you'd like there is
    nothing preventing you from creating Heimdall packages manually. Of course
    Heimdall Frontend provides a simple user interface that takes care of all
    the hard work for you.

    There are two ways in which you can create a firmware package. You can
    create a package from scratch, or you can load an existing package, apply
    modifications and then save the package. Creating a package from scratch
    is the preferred approach, by taking this approach you're far less likely
    to run into file name length limitations. These are not Heimdall's own
    limitation but rather a limitation of the TAR archive format.

    Before you can access Heimdall Frontend's firmware creation functionality
    (available from the "Create Package" tab) you must first specify which
    files will be included in your package, as well as a few flashing options
    i.e. Whether or not users should repartition when flashing. This
    information must be filled out from the "Flash" tab in exactly the same
    fashion you would provide information to flash your device (see "Performing
    a Custom Flash with Heimdall Frontend"). As mentioned above, it's not the
    preferred means, but you're able to load an existing package as a starting
    point for this information.

    Once you've specified the files/partitions you wish to include in your
    firmware package the "Create Package" tab will become available. Clicking
    this tab will display additional information that you can include in your
    package. In order to continue you must fill out all sections except for the
    URLs section, which is optional. The following is a break-down of what all
    these options mean.

    - General Firmware Information: -

        Firmware Name - This is the name of your particular firmware. An
            example would be "Cyanogenmod".

        Firmware Version - This is the version identifier for your package. Any
            valid string will be accepted although a the inclusion of decimal
            point version number is preferred i.e. "7.1". If it makes sense
            then feel free to append a text string like "RC1" or "Beta 1" to
            the decimal point version.

        Platform Name - This is the name of the platform (or operating system)
            that your firmware is based on. In most cases this will simply be
            "Android".

        Platform Version - This is the operating system version that your
            firmware is based on. Again decimal point version numbers are
            preferred over text, i.e. "2.3.4" is preferred over "Gingerbread".


    - Developers -

    URLs (Optional):

        Homepage - Here you can enter your personal URL or a URL particularly
            pertaining to the firmware being packaged. The URL must be well
            formed for it to work. An example of a well formed URL is
            "https://www.glassechidna.com.au/products/heimdall/". It is
            important to include "https://" in order to specify the protocol as
            other protocols such as "ftp://" are equally valid although
            unlikely to be used.

        Donate - Here you can enter a URL that will link users to a page to
            make donations for the effort you've put into developing your
            firmware. Once again the URL must be well formed but there is no
            requirement on how your donation page should work. For instance
            both "https://www.glassechidna.com.au/donate/" and
            "https://forum.xda-developers.com/donatetome.php?u=2710388" are
            equally valid.

    Developer Info:

        Name - Here you can enter in the name of individual team members or a
            team name. Click "Add" and the developer will be added to the list
            on the right. If you make a mistake you can select a developer from
            the list and click "Remove". You can list as many developers as you
            like however visual constraints of the "Load Package" tab means
            only a few names will be visible. Where possible you may want to
            opt for team names over listing individual team members.


    - Supported Devices -

    This section allows you to create a list of devices that are supported by
    your particular firmware. Although Heimdall isn't capable of enforcing this
    we strongly recommend you take this section seriously. If filled out
    correctly you could help save a number of accidental bricks!

    Device Info:

        Manufacturer - This is where you can enter the name of the manufacturer
            for a particular device. For now this will most likely be
            "Samsung".

        Name - This is the human readable name for a particular device.
            "Galaxy S", "Galaxy S II", "Droid Charge", "Vibrant" and
            "Galaxy S (Telstra)" are all valid names. There are a lot of
            possible variations here so be as specific as you think is
            necessary.

        Product Code - This is by far the most important bit of device
            information. Device names tend to be region specific and further
            subject to the whims of telecommunication companies and resellers.
            Product Codes (or product IDs) are designated by manufacturers and
            are generally the definitive means of referring to a particular
            device. Examples are "GT-I9000", "GT-I9100" and "SCH-I897". If
            you're unsure of a particular product code then both Google and
            GSMArena are your friends!


    After filling out all the necessary information the "Build" button will be
    enabled. If it's still disabled then you know you're missing some required
    information. In particular you must specify at least one developer and at
    least one supported device. Pressing the "Build" button will bring up a
    save dialogue where you must chose a file name for your particular package.
    Don't worry about specifying the ".tar.gz" extension Heimdall Frontend will
    take care of this automatically.

    Once you've chosen a file name Heimdall Frontend will begin the process of
    building the firmware package. In doing so a valid firmware.xml file will
    be generated from the information entered. All files will be archived in a
    single TAR file then the TAR archive will be compressed via gzip
    compression. Compression will take a little while but you will see progress
    bars so you know the application hasn't hung. When the progress bars
    disappear you're finished making your package.

    Congratulations! You're now ready to redistribute your firmware package
    online or by any means you see fit.



# Appendix A - firmware.xml

The following details a part of the Heimdall Firmware Package format. This
is only relevant to developers or advanced users who wish to create Heimdall
Firmware Packages outside of Heimdall Frontend or in some way integrate support
for the format in their own software.


All Heimdall Firmware Packages must contain a file called firmware.xml. This
file stores flash information and meta-data for the package as well as
information about other files contained within the package.

The format is fairly straight-forward so it won't be explained in great detail.
Nonetheless the following is an example of a valid firmware.xml file.

<?xml version="1.0" encoding="UTF-8"?>
<firmware version="1">
	<name>Test Firmware</name>
	<version>1.1</version>
	<platform>
		<name>Android</name>
		<version>2.3.4</version>
	</platform>
	<developers>
		<name>Benjamin Dobell</name>
		<name>Hedonism Bot</name>
	</developers>
	<url>https://www.glassechidna.com.au/</url>
	<donateurl>https://www.glassechidna.com.au/donate/</donateurl>
	<devices>
		<device>
			<manufacturer>Samsung</manufacturer>
			<product>GT-I9000</product>
			<name>Galaxy S</name>
		</device>
		<device>
			<manufacturer>Samsung</manufacturer>
			<product>GT-I9000T</product>
			<name>Galaxy S (Telstra)</name>
		</device>
		<device>
			<manufacturer>Samsung</manufacturer>
			<product>GT-I9000M</product>
			<name>Vibrant</name>
		</device>
	</devices>
	<pit>Nl3276-I9000 s1_odin_20100512.pit</pit>
	<repartition>0</repartition>
	<noreboot>0</noreboot>
	<files>
		<file>
			<id>0</id>
			<filename>gq3276-boot.bin</filename>
		</file>
		<file>
			<id>24</id>
			<filename>Uh3276-cache.rfs</filename>
		</file>
		<file>
			<id>22</id>
			<filename>em3276-factoryfs.rfs</filename>
		</file>
		<file>
			<id>11</id>
			<filename>fl3276-modem.bin</filename>
		</file>
		<file>
			<id>21</id>
			<filename>Xd3276-param.lfs</filename>
		</file>
		<file>
			<id>3</id>
			<filename>if3276-Sbl.bin</filename>
		</file>
		<file>
			<id>6</id>
			<filename>cr3276-zImage</filename>
		</file>
	</files>
</firmware>


New lines need not be included and the order in which elements are specified
does not need to match that of the above example.

One and only one <firmware> element must be included. The <firmware> element
must also have a version attribute specified. The version must be parsable as
an integer and indicates what version of the Heimdall Firmware Package
specification the package adheres to.

All data is stored as strings, however a <file>'s <id> element must be parsable
as an integer. The <id> value represents the partition ID (according to the
specified PIT file) that the file should be flashed to.

A <firmware>'s <repartition> and <noreboot> elements must also be parsable as
an integer. However, as they represent boolean values, a value of zero ("0")
means false (or disabled) where as a non-zero value (typically "1") means true
(or enabled).

File names are specified relative to the TAR archive in which firmware.xml and
all other files are to be stored. Heimdall Firmware Packages do not support
directories or links, as such file names should only be a name and not a path.

<url> and <donateurl> are the only optional elements, all other elements must
be included.
