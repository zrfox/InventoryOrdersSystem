This application uses microservice architecure with between
two services (InventoryService & OrdersService) and a
SQL Express database. Additionally, Swagger is used as a 
GUI for simplifying requests when testing application by hand.

Additionally, the solution contains two projects for running
tests: IntegrationTests and OrdersService.Tests, with the latter
being unit tests for OrdersService. 

How to run application:
- requires SQL Express to run
- Docker can be used to run containerized, but is not required. 

How to run tests:
- dotnet test
- tests do not require docker nor SQL Express. 

Unit tests:
- Validate OrdersService functionality for orders being confirmed, 
rejected, and product not found.

Integration tests:
- The tests validate successful operation between 
InventoryService and OrdersService.
- Test 1: On success, confirms an order can be confirmed 
and the product quantity is correctly decremented.
- Test 2: On success, confirms an order placed with 
inadequate inventory is rejected and product quantity
is unaffected. 