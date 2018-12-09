# IoT Edge

## Message flow 
###tempSensor module generates messages
###StoreMessagesToBlob receives messages from tempSensor and stores into Blob storage
###mvconedge populates total blob items and top 10 blob items in table
###mvconedge sends commands: StartUpstream, StopUpstream, CleanBlob to UpstreamFromBlob module
###UpstreamFromBlob module starts in silent mode by default and execute commands from mvconedge module

https://docs.microsoft.com/en-us/azure/iot-edge/

## scripts
sudo iotedge list

sudo iotedge logs tempSensor -f
sudo iotedge logs StoreMessagesToBlob -f
sudo iotedge logs azureBlobStorage -f
sudo iotedge logs UpstreamFromBlob -f
sudo iotedge logs mvconedge -f

sudo systemctl restart iotedge
sudo systemctl status iotedge

docker exec -it <mycontainer> bash

docker stop $(docker ps -a -q)
docker rm $(docker ps -a -q)

docker rmi $(docker images -a -q) --all images

docker container prune
docker image prune -a