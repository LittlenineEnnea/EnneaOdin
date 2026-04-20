Heimdall (c) 2010-2017 Benjamin Dobell, Glass Echidna

# Performing a Custom Flash with Heimdall Frontend

    This is the advanced means of flashing firmware to your device.

    If you're not an advanced user or a developer, in the event that a Heimdall
    Firmware Package doesn't exist for the particular firmware (or files) that
    you wish to flash, then I strongly recommend you get in touch with developer
    of the firmware (or files) and politely ask them to create a Heimdall
    Firmware Package for you. In doing so then you don't have to worry about
    making mistakes due to inexperience.

    If you're looking to customise an existing Heimdall Firmware Package then
    follow steps 1-8 of "Flashing Heimdall Firmware Package with Heimdall
    Frontend" then start from below with step 5.


    1. Fully charge your device (use the wall charger as it's faster).

    2. Download a decrypted device ROM or a Heimdall Firmware Package
       and extract everything to the one directory.

    3. If the ROM is not a Heimdall Firmware Package it may instead be provided
       as multiple archives (nested or otherwise), extract them all to the same
       location.

       NOTE: If you want to use the CSC then extract it last.

    3. Open the Heimdall Frontend (heimdall-frontend) application.

    4. Select the "Flash" tab. From the "Flash" tab you're able to completely
       customise a flash. 

    5. Before you can chose which partitions you want to flash with particular
       files you MUST first select a PIT file. To do this click the "Browse"
       button in the "PIT" section. This will open a dialogue allowing you to
       navigate to and select a valid PIT (.pit) file.

       If you do not already have a valid PIT file stored on your computer you
       can download your device's PIT file from the "Utilities" tab.

    6. If a valid PIT file has been selected then "Add" button below the
       "Partitions (Files)" list-box will become enabled. Press this button to
       add a partition to your flash.

    7. When you first add a partition you will see the "Partition Name" and
       "Partition ID" be populated with information. Use the "Partition Name"
       drop down to select which partition you wish to flash. "Partition ID"
       will automatically update and is never directly editable.

    8. You must then select a file to flash to the partition that you just
       specified using the "Browse" button under the "File / Partition". You
       will not be able to flash, create a firmware package or add another
       partition until you have selected a file. However, you're still able to
       press the "Remove" button if you've decided not to flash the partition
       you've just specified.

    9. When you've specified a file name then you'll be able to see the updated
       information in the partition list to the right. You can select any
       partition from this list and customise it as you see fit.

       You can also remove a partition from the list by selecting it and
       clicking the "Remove" button. Removing a partition from the list doesn't
       remove it from your device, it simply means it will not be flashed.

   10. Repeat steps 7-9 as often as needed to specify all the partitions/files
       that you wish to flash.

   11. Now you can chose whether you would like to repartition your device as
       well as whether you would like to prevent the device rebooting once a
       flash has been completed. These options can be enabled or disabled by
       toggling the "Repartition" and "No Reboot" check-boxes.

       In the general case you will only need to enable repartition if you wish
       to change the PIT file on your device. Keep in mind that repartitioning
       will wipe your device!

       The "No Reboot" option is rarely required. It's mostly in place so you
       can manually boot straight into recovery mode after a flash (rather than
       booting up normally).

   12. If you've added at least one partition to your flash (and selected a
       file for that partition) then the "Start" button will be enabled. Press
       the "Start" button to begin the flashing process.

       You may notice that the "Create Package" tab becomes available at the
       whenever the "Start" button becomes available. From this tab you're able
       to create a reusable, redistributable Heimdall Firmware Package with the
       files and partitions you just selected. See "How to Create a Heimdall
       Firmware Package" for details.

   13. Heimdall Frontend will display the progress and inform you when the
       flash is complete.

       If something went wrong i.e. your device wasn't detected because it
       wasn't in download mode, then the status section will let you know the
       cause of the problem.
