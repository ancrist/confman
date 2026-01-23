#!/bin/bash

dotnet run --no-launch-profile --urls "http://127.0.0.1:6001" -- --publicEndPoint="http://127.0.0.1:6001/" --coldStart=false --standby=false --members:0="http://127.0.0.1:6000/" --members:1="http://127.0.0.1:6001/" --members:2="http://127.0.0.1:6002/"