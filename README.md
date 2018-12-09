# IoT Edge

## Message flow 
1. tempSensor module generates messages
2. StoreMessagesToBlob receives messages from tempSensor and stores into Blob storage
3. mvconedge populates total blob items and top 10 blob items in table
4. mvconedge sends commands: StartUpstream, StopUpstream, CleanBlob to UpstreamFromBlob module; reset to tempSensor module
5. UpstreamFromBlob module starts in silent mode by default and execute commands from mvconedge module
6. samodule sends reset command to tempSensor if the average machine temperature in a 30-second window reaches 70 degrees

https://docs.microsoft.com/en-us/azure/iot-edge/

## scripts

module commands:
* sudo iotedge list
* sudo iotedge restart tempSensor

module logs:
* sudo iotedge logs tempSensor -f
* sudo iotedge logs StoreMessagesToBlob -f
* sudo iotedge logs azureBlobStorage -f
* sudo iotedge logs UpstreamFromBlob -f
* sudo iotedge logs mvconedge -f
* sudo iotedge logs samodule -f

serice iotedge:
* sudo systemctl restart iotedge
* sudo systemctl status iotedge

docker exec -it <mycontainer> bash

docker commands with containers:
* docker stop $(docker ps -a -q)
* docker rm $(docker ps -a -q)
* docker container prune

docker commands with images:
* docker rmi $(docker images -a -q) --all images
* docker container prune
* docker image prune -a